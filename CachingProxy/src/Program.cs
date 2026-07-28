using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;
using Amazon.Extensions.NETCore.Setup;
using Amazon.Runtime;
using Amazon.S3;
using DotNetEnv.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
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
        .AddRuntimeInstrumentation()
        // explicit configuration of AspNetCoreInstrumentation
        .AddMeter(
          "Microsoft.AspNetCore.Hosting",
          // "Microsoft.AspNetCore.Server.Kestrel",
          "Microsoft.AspNetCore.Http.Connections",
          "Microsoft.AspNetCore.Routing",
          "Microsoft.AspNetCore.Diagnostics",
          "Microsoft.AspNetCore.RateLimiting",
          "Microsoft.AspNetCore.Components",
          "Microsoft.AspNetCore.Components.Server.Circuits",
          "Microsoft.AspNetCore.Components.Lifecycle",
          "Microsoft.AspNetCore.Authorization",
          "Microsoft.AspNetCore.Authentication",
          "Microsoft.AspNetCore.Identity",
          "Microsoft.AspNetCore.MemoryPool")
        // Inbound duration sits at 15 of Prometheus's 16 labels, so it is one tag away from the
        // same scrape failure the client metrics caused: ASP.NET Core adds http.route once an
        // endpoint matches and error.type on unhandled exceptions. Pre-emptively drop the tags
        // that carry no signal here -- url.scheme is always http behind the ingress, the protocol
        // version is uniformly 1.1, and this proxy serves everything from catch-all middleware so
        // http.route is "(missing)" rather than a real route.
        .AddView("http.server.request.duration", new MetricStreamConfiguration
        {
          TagKeys =
          ["http.request.method", "http.response.status_code", "error.type", "aspnetcore.request.is_unhandled"]
        })
        // Outbound instrumentation: http.client.* (request duration incl. error.type,
        // open_connections, connection duration) and dns.lookup.duration. Upstream connect
        // failures are otherwise invisible -- they are folded into NEGATIVE_MISS alongside
        // genuine 404s, so they cannot be told apart in caching_requests_total.
        .AddMeter(
          "System.Net.Http",
          "System.Net.NameResolution")
        // Trim the http.client.* tag sets. Two independent reasons:
        // 1. Prometheus counts target labels (app, instance, namespace, pod index, node, version, ...)
        //    plus __name__ towards its per-series label_limit (16 for kubernetes-pods). The default
        //    9 tags on http.client.request.duration_bucket push the total to 17 and the whole scrape
        //    is rejected, not just that one metric.
        // 2. network.peer.address is unbounded: upstreams are CDNs whose IPs rotate, so every new
        //    edge IP forks a fresh series (x17 for a histogram) that never gets written to again.
        // server.address is the dimension we actually slice by; port/scheme/protocol are constant
        // per upstream and recoverable from it.
        .AddView("http.client.request.duration", new MetricStreamConfiguration
        {
          TagKeys = ["server.address", "http.request.method", "http.response.status_code", "error.type"]
        })
        .AddView("http.client.request.time_in_queue", new MetricStreamConfiguration
        {
          TagKeys = ["server.address", "http.request.method"]
        })
        .AddView("http.client.connection_duration", new MetricStreamConfiguration
        {
          TagKeys = ["server.address"]
        })
        .AddView("http.client.open_connections", new MetricStreamConfiguration
        {
          TagKeys = ["server.address", "http.connection.state"]
        })
        .AddMeter(CachingProxyMetrics.MeterName)
        .AddPrometheusExporter()
        .AddOtlpExporter()
      );

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

      // Single shared connection used by both the L2 cache and the health check below. Resolves
      // lazily (on first cache/health-check use), so startup is not blocked on connecting to Redis.
      services.AddSingleton<IConnectionMultiplexer>(
        _ => ConnectionMultiplexer.Connect(redis.ConnectionString));

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
        .WithRegisteredDistributedCache();
    }

    services
      .AddRouting()
      .AddMemoryCache()
      .AddOptions<MemoryCacheOptions>()
      .Configure<TimeProvider>((options, tp) => options.Clock = new TimeProviderClock(tp));

    if (!string.IsNullOrEmpty(context.Configuration.Get<CachingProxyConfig>()?.S3?.BucketName))
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
      // it (and the static-file options that depend on it) must not be registered in S3 mode.
      services
        .AddSingleton<IContentTypeProvider>(_ => new FileExtensionContentTypeProvider
        {
          Mappings =
          {
            [".pom"] = "application/x-maven-pom+xml",
            [".ivy"] = "application/x-ivy+xml",
            [".nuspec"] = "application/x-nuspec+xml",
            [".jnlp"] = "application/x-java-jnlp-file",
            [".sha1"] = "application/x-checksum",
            [".sha256"] = "application/x-checksum",
            [".sha512"] = "application/x-checksum",
            [".md5"] = "application/x-checksum",
            [".jar"] = "application/java-archive",
            [".war"] = "application/java-archive",
            [".ear"] = "application/java-archive",
            [".sar"] = "application/java-archive",
            [".har"] = "application/java-archive",
            [".hpi"] = "application/java-archive",
            [".jpi"] = "application/java-archive"
          }
        })
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
    app.UseRouting();
    app.UseHealthChecks("/health");
    app.UseInboundAuth();
    if (!string.IsNullOrEmpty(configuration.Get<CachingProxyConfig>()!.S3?.BucketName))
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
}
