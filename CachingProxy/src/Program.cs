using System;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    }
    else
    {
      builder.Logging.AddJsonConsole();
      builder.Logging.AddSentry();
    }

    // Bind CachingProxyConfig from configuration
    builder.Services.Configure<CachingProxyConfig>(builder.Configuration);

    ConfigureOurServices(builder.Services);

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

    app.UseMiddleware<CachingProxy>();

    return app.RunAsync();
  }

  public static void ConfigureOurServices(IServiceCollection services)
  {
    var timeProvider = TimeProvider.System;
    services
      .AddSingleton(timeProvider)
      .AddMemoryCache(options => options.Clock = new TimeProviderClock(timeProvider))
      .AddSingleton<CachingProxyMetrics>()
      .AddHostedService<CleanupService>()
      .AddSingleton<ResponseCache>()
      .AddHttpClient<ProxyHttpClient>(static (provider, client) =>
      {
        var config = provider.GetRequiredService<IOptions<CachingProxyConfig>>().Value;
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
}
