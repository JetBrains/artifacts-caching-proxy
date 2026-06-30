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

    builder.Services
      .ConfigureOurServices(builder.Configuration);

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

  public static IServiceCollection ConfigureOurServices(this IServiceCollection services, IConfiguration configuration)
  {
    services
      .AddSingleton(TimeProvider.System)
      .AddSingleton<CachingProxyMetrics>()
      .AddSingleton<ResponseCache>()
      .AddSingleton<RemoteProxy>()
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
    var redis = configuration.Get<CachingProxyConfig>()?.Redis;
    if (!string.IsNullOrEmpty(redis?.ConnectionString))
    {
      // CachedResponse holds an IHeaderDictionary that MemoryPack can't serialize on its own, so a
      // custom formatter (registered globally) maps it to/from a serializable surrogate.
      CachedResponseFormatter.Register();

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

    if (!string.IsNullOrEmpty(configuration.Get<CachingProxyConfig>()?.S3?.BucketName))
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
      .AddUpstreamAuth(configuration)
      .AddInboundAuth(configuration);

    services
      .AddHttpClient<ProxyHttpClient>(static (provider, client) =>
      {
        var config = provider.GetRequiredService<CachingProxyConfig>();
        client.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSec);
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        // The base product token is added globally (ConfigureHttpClientDefaults); only append the optional
        // deployment-specific comment here.
        if (config.UserAgentComment is { Length: >0 } userAgentComment)
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
      .UseSocketsHttpHandler(static (handler, _) =>
      {
        // force reconnection (and DNS re-resolve) every ten minutes
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
        handler.AllowAutoRedirect = true;
        handler.AutomaticDecompression = DecompressionMethods.None;
        handler.UseCookies = false;
      })
      .AddTransientHttpErrorPolicy(static policyBuilder => policyBuilder.WaitAndRetryAsync(
        4, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1))));

    return services;
  }

  public static void ConfigureOurApp(this IApplicationBuilder app, IConfiguration configuration)
  {
    var cachingProxyConfig = configuration.Get<CachingProxyConfig>()!;
    app.UseRouting();
    app.UseHealthChecks("/health");
    app.UseInboundAuth(cachingProxyConfig);
    if (!string.IsNullOrEmpty(cachingProxyConfig.S3?.BucketName))
    {
      app.UseMiddleware<S3CachingMiddleware>();
    }
    else
    {
      app.UseMiddleware<CachingProxy>();
    }
    app.UseEndpoints(builder =>
    {
      builder.DataSources.Add(new RemoteServers(cachingProxyConfig));
    });

  }
}
