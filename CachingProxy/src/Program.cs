using System;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;
using Amazon.S3;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Polly;

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
      .AddSingleton(sp => sp.GetRequiredService<IOptions<CachingProxyConfig>>().Value);

    ConfigureOurServices(builder.Services, builder.Configuration);

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

    ConfigureOurApp(app, app.Configuration);

    return app.RunAsync();
  }

  public static void ConfigureOurServices(IServiceCollection services, IConfiguration configuration)
  {
    services
      .AddSingleton(TimeProvider.System)
      .AddSingleton<CachingProxyMetrics>()
      .AddSingleton<ResponseCache>()
      .AddSingleton<RemoteProxy>()
      .AddSingleton<CacheFileProvider>()
      .ConfigureOptions<ConfigureStaticFileMiddleware>()
      .AddMemoryCache()
      .AddOptions<MemoryCacheOptions>()
      .Configure<TimeProvider>((options, tp) => options.Clock = new TimeProviderClock(tp));

    if (configuration.Get<CachingProxyConfig>()?.S3?.BucketName  is not null)
    {
      // AWSOptions resolves the configured profile (including SSO) into credentials when the client is
      // created, so a named profile and the default credential chain go through the same registration.
      services
        .AddDefaultAWSOptions(configuration.GetAWSOptions())
        .AddAWSService<IAmazonS3>()
        .AddSingleton<S3CachingMiddleware>()
        .AddHealthChecks()
        .AddCheck<S3CachingMiddleware>(nameof(S3CachingMiddleware));
    }
    else
    {
      services
        .AddSingleton(sp => sp.GetRequiredService<IOptions<StaticFileOptions>>().Value)
        .AddScoped<CachingProxy>()
        .AddHostedService<CleanupService>()
        .AddHealthChecks()
        .AddCheck<CachingProxy>(nameof(CachingProxy));
    }

    services
      .AddHttpClient<ProxyHttpClient>(static (provider, client) =>
      {
        var config = provider.GetRequiredService<CachingProxyConfig>();
        client.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSec);
        var userAgentHeader = client.DefaultRequestHeaders.UserAgent;
        userAgentHeader.Add(ourUserAgent);
        if (config.UserAgentComment is { Length: >0 } userAgentComment)
        {
          try
          {
            userAgentHeader.Add(new ProductInfoHeaderValue(userAgentComment));
          }
          catch (FormatException ex)
          {
            provider.GetService<ILogger>()?.LogError(ex, "An error occurred while parsing the user-agent comment.");
          }
        }
      })
      .UseSocketsHttpHandler(static (handler, _) =>
      {
        // force reconnection (and DNS re-resolve) every two minutes
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
        handler.UseCookies = false;
      })
      .AddTransientHttpErrorPolicy(static policyBuilder => policyBuilder.WaitAndRetryAsync(
        4, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1))));
  }

  public static void ConfigureOurApp(IApplicationBuilder app, IConfiguration configuration)
  {
    app.UseHealthChecks("/health");
    if (configuration.Get<CachingProxyConfig>()?.S3?.BucketName is not null)
    {
      app.UseMiddleware<S3CachingMiddleware>();
    }
    else
    {
      app
        .UseStaticFiles()
        .UseMiddleware<CachingProxy>();
    }
  }
}
