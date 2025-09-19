using System;
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
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
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
    services
      .AddSingleton<CachingProxyMetrics>()
      .AddHttpClient<ProxyHttpClient>((provider, client) =>
      {
        var config = provider.GetRequiredService<IOptions<CachingProxyConfig>>().Value;
        client.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSec);
      })
      .UseSocketsHttpHandler((handler, _) =>
      {
        // force reconnection (and DNS re-resolve) every two minutes
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
        handler.UseCookies = false;
      })
      .AddTransientHttpErrorPolicy(policyBuilder => policyBuilder.WaitAndRetryAsync(
        4, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1))));
  }
}
