using System.Diagnostics.CodeAnalysis;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Sentry;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public class HealthCheck(IOptions<SentryOptions> sentryOptions) : IHealthCheck, IConfigureOptions<HealthCheckOptions>
{
  public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    return Task.FromResult(HealthCheckResult.Healthy(sentryOptions.Value?.Release));
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
