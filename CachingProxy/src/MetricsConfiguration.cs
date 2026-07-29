using OpenTelemetry.Metrics;

namespace JetBrains.CachingProxy;

/// <summary>
/// Meter selection and stream shaping for the metrics this service publishes. Kept out of
/// <see cref="Program"/> and exporter-free so a test can build the same configuration and count what a
/// Prometheus scrape would see: the limits below are only breached in aggregate, never at one call site.
/// </summary>
public static class MetricsConfiguration
{
  // Prometheus rejects a whole scrape that exceeds sample_limit, and the cap is set per deployment, so it
  // is not ours to raise. Histogram buckets dominate the exposition: an untouched *.duration instrument
  // inherits .NET's 14 advice boundaries and costs 17 samples per series. These sets cost 2-9.
  //
  // Series never retire while the process lives, so a long-running pod keeps finding new
  // upstream/status combinations -- boundaries, not tags, are the lever that keeps working.
  //
  // Both latency sets are cut against measured production distributions. Decade steps looked tidy but
  // put nearly all requests in one bucket, which tells us no more than the mean.

  // Inbound is mostly cache hits and redirects that never leave the process, and the shape differs per
  // deployment: local-disk hits land below a millisecond, object-storage hits a few milliseconds later.
  // Boundaries straddle a millisecond so one set resolves either.
  private static readonly double[] ourInboundLatencyBoundaries = [0.0005, 0.002, 0.01, 0.1, 1, 10];

  // Outbound is a mixture, and the spread is between upstreams rather than within one: per-upstream means
  // span two decades, in-region object storage to distant CDNs. So the low boundaries separate the near
  // upstreams, which 100ms swallowed together, and the top one reaches past ConnectTimeoutSec, where a
  // request is failing rather than slow.
  private static readonly double[] ourUpstreamLatencyBoundaries = [0.025, 0.1, 0.5, 2.5, 10];

  // PooledConnectionLifetime caps connections at ten minutes; the only question is whether they reach it.
  private static readonly double[] ourConnectionLifetimeBoundaries = [10, 600];

  // No boundaries emits no _bucket lines at all, leaving sum and count: 2 samples per series. For streams
  // whose distribution we would not act on.
  private static readonly double[] ourCountAndSumOnly = [];

  public static MeterProviderBuilder ConfigureOurMetrics(this MeterProviderBuilder metrics) => metrics
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
    // Drop the tags that carry no signal here: url.scheme is always http behind the ingress, the protocol
    // version is uniformly 1.1, and this proxy serves everything from catch-all middleware so http.route
    // is "(missing)". Untrimmed it sits one tag below Prometheus's label_limit.
    .AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration
    {
      TagKeys =
      ["http.request.method", "http.response.status_code", "error.type", "aspnetcore.request.is_unhandled"],
      Boundaries = ourInboundLatencyBoundaries
    })
    // Authentication timing is a yes/no health signal: the scheme either validates tokens or it does not.
    .AddView("aspnetcore.authentication.authenticate.duration", new ExplicitBucketHistogramConfiguration
    {
      Boundaries = ourCountAndSumOnly
    })
    // Outbound instrumentation. Without it, upstream connect failures are invisible: caching_requests_total
    // folds them into NEGATIVE_MISS alongside genuine 404s.
    .AddMeter(
      "System.Net.Http",
      "System.Net.NameResolution")
    // Trim the http.client.* tag sets, for two independent reasons:
    // 1. The default 9 tags plus target labels and __name__ exceed Prometheus's per-series label_limit,
    //    which rejects the whole scrape rather than the offending metric.
    // 2. network.peer.address is unbounded: upstreams are CDNs whose IPs rotate, so every new edge IP
    //    forks a series that never gets written to again.
    // server.address is what we slice by; port/scheme/protocol are constant per upstream. Unlike inbound,
    // http.request.method is dropped -- it just multiplies the widest stream we publish, and the proxy
    // mirrors the inbound verb, so http.server.request.duration already carries it.
    .AddView("http.client.request.duration", new ExplicitBucketHistogramConfiguration
    {
      TagKeys = ["server.address", "http.response.status_code", "error.type"],
      Boundaries = ourUpstreamLatencyBoundaries
    })
    // Queue time answers "is MaxConnectionsPerServer throttling this upstream", and the remedy is the same
    // whatever the shape of the wait.
    .AddView("http.client.request.time_in_queue", new ExplicitBucketHistogramConfiguration
    {
      TagKeys = ["server.address"],
      Boundaries = ourCountAndSumOnly
    })
    // NOTE: dotted -- "connection.duration", not "connection_duration". A view matching no instrument is
    // silently inert, and the exporter sanitizes both spellings to the same Prometheus name, so the scrape
    // cannot tell a matched view from an unmatched one. That typo shipped, and the stream went out with the
    // advice boundaries and the peer IP still attached. Take these names from the runtime, not the scrape.
    .AddView("http.client.connection.duration", new ExplicitBucketHistogramConfiguration
    {
      TagKeys = ["server.address"],
      Boundaries = ourConnectionLifetimeBoundaries
    })
    .AddView("http.client.open_connections", new MetricStreamConfiguration
    {
      TagKeys = ["server.address", "http.connection.state"]
    })
    // dns.question.name is the upstream host, so it is bounded by the configured remote list and worth
    // keeping -- but whether resolutions fail matters far more than which are slow.
    .AddView("dns.lookup.duration", new ExplicitBucketHistogramConfiguration
    {
      TagKeys = ["dns.question.name", "error.type"],
      Boundaries = ourCountAndSumOnly
    })
    .AddMeter(CachingProxyMetrics.MeterName);
}
