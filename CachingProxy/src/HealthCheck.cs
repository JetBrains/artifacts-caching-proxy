using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Mime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public class HealthCheck : IHealthCheck, IConfigureOptions<HealthCheckOptions>
{
  private Task<HealthCheckResult>? mySuccessCheck;

  public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    return mySuccessCheck ??= Task.FromResult(HealthCheckResult.Healthy(
      Environment.GetEnvironmentVariable("SENTRY_RELEASE") ??
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));
  }

  public void Configure(HealthCheckOptions options)
  {
    options.ResponseWriter = static async (writer, result) =>
    {
      writer.Response.ContentType = MediaTypeNames.Text.Plain;
      foreach (var (key, report) in result.Entries)
      {
        await writer.Response.WriteAsync($"{key}: {report.Description ?? report.Status.ToString()}\n", writer.RequestAborted);
      }
    };
  }
}
