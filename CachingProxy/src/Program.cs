using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Reflection;
using System.Threading.Tasks;
using Amazon.Extensions.NETCore.Setup;
using Amazon.Runtime;
using Amazon.S3;
using DotNetEnv.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Polly;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.CysharpMemoryPack;

namespace JetBrains.CachingProxy;

public static class Program
{
  private static readonly ProductInfoHeaderValue ourUserAgent;

  static Program()
  {
    var executingAssembly = Assembly.GetExecutingAssembly();
    var name = executingAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? executingAssembly.GetName().Name!;
    var version = executingAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? executingAssembly.GetName().Version?.ToString();
    ourUserAgent = new ProductInfoHeaderValue(name, version);
  }

  public static Task Main(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);

    builder.WebHost.UseSentry();

    if (builder.Environment.IsDevelopment())
    {
      builder.Logging.AddSimpleConsole();
      builder.Configuration
        .AddDotNetEnv()
        .AddJsonFile("appsettings.Development.user.json", optional: true, reloadOnChange: true);
    }
    else
    {
      builder.Logging.AddJsonConsole();
      builder.Logging.AddSentry();
    }

    // Bind CachingProxyConfig from configuration
    builder.Services
      .Configure<CachingProxyConfig>(builder.Configuration)
      .AddSingleton(static sp => sp.GetRequiredService<IOptions<CachingProxyConfig>>().Value);

    builder.WebHost.ConfigureOurServices();

    builder.Services
      .AddOpenTelemetry()
      .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
      .WithMetrics(metrics => metrics
        .ConfigureOurMetrics()
        .AddPrometheusExporter()
        .AddOtlpExporter());

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
      app.UseDeveloperExceptionPage();
    }

    app.UseOpenTelemetryPrometheusScrapingEndpoint();

    app.ConfigureOurApp(app.Configuration);

    return app.RunAsync();
  }

  public static IWebHostBuilder ConfigureOurServices(this IWebHostBuilder webHostBuilder) => webHostBuilder.ConfigureServices((context, services) =>
  {
    services
      .AddSingleton(TimeProvider.System)
      .AddSingleton<CachingProxyMetrics>()
      .AddSingleton<ResponseCache>()
      .AddSingleton<RemoteProxy>()
      .AddSingleton<RemoteServers>()
      .ConfigureOptions<HealthCheck>()
      .AddHealthChecks()
      .AddCheck<HealthCheck>(nameof(HealthCheck));

    // Default User-Agent for every HttpClient created by IHttpClientFactory (the proxy client, the GitHub
    // App REST client, and Duende's token client). Per-client configuration runs after this, so callers can
    // still append to it (e.g. ProxyHttpClient adds the optional UserAgentComment).
    services.ConfigureHttpClientDefaults(builder =>
      builder.ConfigureHttpClient(client => client.DefaultRequestHeaders.UserAgent.Add(ourUserAgent)));

    var fusionCacheBuilder = services
      .AddFusionCache()
      .AsHybridCache()
      // Use the DI-registered IMemoryCache (configured below with a TimeProvider-backed clock)
      // instead of FusionCache's own internal MemoryCache, so the configured clock actually applies.
      .WithRegisteredMemoryCache();

    // Opt-in L2 (distributed) cache: wired only when a Redis connection string is configured, so
    // disk/dev runs stay L1-only with no Redis dependency. Mirrors the S3 conditional below. The
    // per-status DistributedCacheDuration applied in ResponseCache.PutStatusCode takes effect once this runs.
    var redis = context.Configuration.Get<CachingProxyConfig>()?.Redis;
    if (!string.IsNullOrEmpty(redis?.ConnectionString))
    {
      // CachedResponse holds an IHeaderDictionary that MemoryPack can't serialize on its own, so a
      // custom formatter (registered globally) maps it to/from a serializable surrogate.
      CachedResponseFormatter.Register();
      // Duende's client-credentials token cache also flows through this FusionCache (as HybridCache),
      // so its (non-MemoryPackable) ClientCredentialsToken needs a formatter too, else L2 serialization
      // fails and Duende falls back to no token caching.
      ClientCredentialsTokenFormatter.Register();

      // Parsed up front rather than handed over as a string, so the timeouts below are explicit. One
      // multiplexer carries the whole process's L2 traffic, so a command going nowhere has to vacate
      // the connection quickly - anything still queued behind a stalled read waits it out.
      var redisOptions = ConfigurationOptions.Parse(redis.ConnectionString);
      // Server-side latency runs ~33ms, so 1s is ample headroom while giving up on a doomed command
      // five times sooner than StackExchange.Redis' 5s default.
      redisOptions.SyncTimeout = 1000;
      redisOptions.AsyncTimeout = 1000;
      // An unreachable Redis at boot must not stop the proxy starting: it degrades to L1-only.
      redisOptions.AbortOnConnectFail = false;

      // Single shared connection used by both the L2 cache and the health check below. Resolves
      // lazily (on first cache/health-check use), so startup is not blocked on connecting to Redis.
      services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));

      services.AddStackExchangeRedisCache(_ => { });
      services.AddOptions<RedisCacheOptions>()
        .Configure<IConnectionMultiplexer>((options, mux) =>
        {
          options.ConnectionMultiplexerFactory = () => Task.FromResult(mux);
          if (!string.IsNullOrEmpty(redis.InstanceName))
            options.InstanceName = redis.InstanceName;
        });

      services.AddHealthChecks()
        .AddRedis(sp => sp.GetRequiredService<IConnectionMultiplexer>(), failureStatus: HealthStatus.Degraded, name: "redis");

      fusionCacheBuilder
        .WithSerializer(new FusionCacheCysharpMemoryPackSerializer())
        .WithRegisteredDistributedCache()
        // A saturated Redis connection must not hold up a request: L2 is an optimization, so a proxy
        // that cannot reach it should fall through to S3/upstream rather than wait out
        // StackExchange.Redis' 5s command timeout. Only the hard timeout is load-bearing here - the
        // soft one applies solely with fail-safe enabled and a stale value to serve, and fail-safe is off.
        .WithDefaultEntryOptions(options =>
        {
          options.DistributedCacheHardTimeout = TimeSpan.FromMilliseconds(500);
          // Sets and refreshes stop blocking the request that triggered them.
          options.AllowBackgroundDistributedCacheOperations = true;
        });
    }

    services
      .AddRouting()
      .AddMemoryCache()
      .AddOptions<MemoryCacheOptions>()
      .Configure<TimeProvider>((options, tp) => options.Clock = new TimeProviderClock(tp));

    if (context.Configuration.Get<CachingProxyConfig>()?.IsS3Mode is true)
    {
      services
        .AddSingleton<AWSOptions>(static provider =>
        {
          // AWSOptions resolves the configured profile (including SSO) into credentials when the client is
          // created, so a named profile and the default credential chain go through the same registration.
          var awsOptions = provider.GetRequiredService<IConfiguration>().GetAWSOptions();
          // S3 answers sustained write load with "SlowDown" (HTTP 503). Standard retry handles this with
          // jittered exponential backoff (no client-side rate limiter). Raise MaxErrorRetry so brief
          // throttling bursts are absorbed by the SDK instead of escaping to the client. (Applies to the
          // whole client, so the prefetch GetObject is protected too, not just PutObject.)
          awsOptions.DefaultClientConfig.RetryMode = RequestRetryMode.Standard;
          awsOptions.DefaultClientConfig.MaxErrorRetry = 8;
          return awsOptions;
        })
        .AddAWSService<IAmazonS3>();
    }
    else
    {
      // Disk-only services: CacheFileProvider validates/creates LocalCachePath in its constructor, so
      // it must not be registered in S3 mode.
      services
        .AddHostedService<CleanupService>()
        .AddHealthChecks()
        .AddCheck<CachingProxy.HealthCheck>(nameof(CachingProxy));
    }

    services
      .AddUpstreamAuth(context.Configuration)
      .AddInboundAuth(context.Configuration, context.HostingEnvironment);

    services
      .AddHttpClient<ProxyHttpClient>(static (provider, client) =>
      {
        var config = provider.GetRequiredService<CachingProxyConfig>();
        client.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSec);
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        // The base product token is added globally (ConfigureHttpClientDefaults); only append the optional
        // deployment-specific comment here.
        if (config.UserAgentComment is { Length: > 0 } userAgentComment)
        {
          try
          {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(userAgentComment));
          }
          catch (FormatException ex)
          {
            provider.GetService<ILogger>()?.LogError(ex, "An error occurred while parsing the user-agent comment.");
          }
        }
      })
      .UseSocketsHttpHandler(static (handler, provider) =>
      {
        var config = provider.GetRequiredService<CachingProxyConfig>();

        // force reconnection (and DNS re-resolve) every ten minutes
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
        handler.AllowAutoRedirect = true;
        handler.AutomaticDecompression = DecompressionMethods.None;
        handler.UseCookies = false;

        handler.ConnectTimeout = TimeSpan.FromSeconds(config.ConnectTimeoutSec);
        handler.MaxConnectionsPerServer = config.MaxConnectionsPerServer;

        // NOTE on "Cannot assign requested address" (EADDRNOTAVAIL) when connecting upstream:
        // .NET's resolver does not pass AI_ADDRCONFIG, so AAAA records are returned even where no
        // routable IPv6 source address exists (archive.apache.org and downloads.apache.org publish
        // both A and AAAA). On an IPv4-only host the dual-mode socket then attempts the IPv6
        // address and fails instantly, and because the multi-address connect loop reports the
        // *last* error, that bogus attempt can also mask why an IPv4 attempt failed.
        //
        // This is deliberately NOT handled here: the proxy stays dual-stack capable, and it is the
        // individual deployment's job to declare that its network is IPv4-only by setting
        // DOTNET_SYSTEM_NET_DISABLEIPV6=1 (which makes Socket.OSSupportsIPv6 false, so the handler
        // creates an AF_INET socket and skips AAAA results).
      })
      .AddTransientHttpErrorPolicy(static policyBuilder => policyBuilder.WaitAndRetryAsync(
        4, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1))));
  });

  public static void ConfigureOurApp(this IApplicationBuilder app, IConfiguration configuration)
  {
    var config = configuration.Get<CachingProxyConfig>()!;
    app.UseRouting();
    app.UseHealthChecks("/health");
    app.UseInboundAuth();
    app.UseOciPing(config.InboundAuth != null);
    if (config.IsS3Mode)
    {
      app.UseMiddleware<S3CachingMiddleware>();
    }
    else
    {
      app.UseMiddleware<CachingProxy>();
    }
    app.UseEndpoints(endpoints =>
    {
      endpoints.DataSources.Add(endpoints.ServiceProvider.GetRequiredService<RemoteServers>());
    });
  }

  /// <summary>
  /// The OCI distribution base endpoint. Every registry client probes <c>GET /v2/</c> before it fetches
  /// anything and gives up on the pull unless that answers 2xx or 401; the configured prefixes are
  /// <c>/v2/&lt;alias&gt;</c>, so the bare probe would otherwise fall through to a 404.
  /// <para>An exact path match, not <c>app.Map("/v2")</c>, which would swallow every
  /// <c>/v2/&lt;alias&gt;</c> request with it.</para>
  /// <para>Registered after the inbound auth, because a registry client fixes its auth strategy for the
  /// whole host from this probe alone: a 200 with no WWW-Authenticate means "anonymous registry", and the
  /// client then never sends the <c>docker login</c> credentials - the 401 on the manifest that follows is
  /// terminal rather than a prompt (and <c>docker login</c> itself stores the credentials unvalidated). So
  /// an unauthenticated probe has to be challenged whenever a gated OCI prefix exists. It routes no request
  /// of its own, so <c>UseAuthorization</c> has no endpoint metadata to enforce here and the challenge is
  /// issued explicitly.</para>
  /// </summary>
  private static void UseOciPing(this IApplicationBuilder app, bool inboundAuthConfigured)
  {
    // Both halves are fixed at startup, so resolve them once instead of per request. Without InboundAuth
    // there is nothing a client could present, and challenging would only make the registry unusable.
    var challengeTheProbe = inboundAuthConfigured &&
                            app.ApplicationServices.GetRequiredService<RemoteServers>().HasGatedOciPrefix;

    app.Use(async (context, next) =>
    {
      var path = context.Request.Path;
      var isBare = path.Equals("/v2", StringComparison.OrdinalIgnoreCase);
      if (!isBare && !path.Equals("/v2/", StringComparison.OrdinalIgnoreCase) ||
          !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
      {
        await next(context);
        return;
      }

      // A real registry answers the bare /v2 with a redirect to /v2/, and clients follow it. 307 rather than
      // 302 to keep the method, matching the redirector's own `location = /v2`.
      if (isBare)
      {
        context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
        context.Response.Headers.Location = "/v2/";
        return;
      }

      // On the 401 too: a client reads the version header off whichever response the probe returns.
      context.Response.Headers[CachingProxyConstants.DockerApiVersionHeader] = CachingProxyConstants.DockerApiVersion;

      if (challengeTheProbe && context.User.Identity?.IsAuthenticated != true)
      {
        // The default scheme's challenge, so the probe advertises the same Basic realm as every other 401.
        await context.ChallengeAsync();
        return;
      }

      context.Response.ContentType = MediaTypeNames.Application.Json;
      // An empty JSON object is what the spec's "2xx with no meaningful body" amounts to in practice, and
      // what every registry returns.
      await context.Response.WriteAsync("{}", context.RequestAborted);
    });
  }
}
