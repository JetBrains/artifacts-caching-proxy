using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace JetBrains.CachingProxy;

/// <summary>
/// Redis reachability by PING. The AspNetCore.HealthChecks.Redis check sends CLUSTER INFO to a cluster
/// endpoint, which StackExchange.Redis 3 refuses as an admin command, reporting Redis degraded on every check.
/// </summary>
public class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    try
    {
      await redis.GetDatabase().PingAsync();
      return HealthCheckResult.Healthy();
    }
    catch (Exception e)
    {
      return new HealthCheckResult(context.Registration.FailureStatus, exception: e);
    }
  }
}
