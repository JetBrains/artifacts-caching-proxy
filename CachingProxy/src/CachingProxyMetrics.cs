using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace JetBrains.CachingProxy;

public class CachingProxyMetrics
{
  public static readonly string MeterName = typeof(CachingProxyMetrics).Namespace!;

  private readonly Counter<long> myRequestsCounter;

  public CachingProxyMetrics(IMeterFactory meterFactory)
  {
    var meter = meterFactory.Create(MeterName);
    myRequestsCounter = meter.CreateCounter<long>("caching_requests");
  }

  public void IncrementRequests(CachingProxyStatus status)
  {
    myRequestsCounter.Add(1, new KeyValuePair<string, object?>(nameof(status), status.ToString()));
  }
}
