using OpenTelemetry.Metrics;

namespace JetBrains.CachingProxy;

/// <summary>
/// Meter selection and stream shaping for the metrics this service publishes. Kept out of
/// <see cref="Program"/> and exporter-free so a test can build the exact same configuration and count
/// what a Prometheus scrape would see -- the two guardrails below are only violated in aggregate, which
/// is not something the code reads as wrong at any single call site.
/// </summary>
public static class MetricsConfiguration
{
  // Prometheus caps the number of samples it accepts from one scrape (sample_limit), and histogram
  // buckets dominate this exposition: 1717 of 1857 samples in production. Every *.duration instrument
  // inherits .NET's advice boundaries, which are 14 wide (0.005s..10s), so a single series costs 17
  // samples -- 15 buckets plus sum and count. The boundary sets below trade quantile resolution for a
  // per-series cost of 3-7 samples.
  //
  // Boundaries, not tags, are the lever that lasts: these streams are cumulative and series never retire
  // while the process lives, so a long-running pod keeps discovering new upstream/method/status
  // combinations and any budget built on today's cardinality erodes back over the limit.

  // Inbound latency spans the widest range, because most requests are answered from cache or with a
  // redirect and never leave the process: 1ms is a memory hit, 10ms a disk hit, and anything past 100ms
  // means the request went upstream.
  private static readonly double[] ourInboundLatencyBoundaries = [0.001, 0.01, 0.1, 1, 10];

  // Outbound latency starts an order of magnitude later -- an upstream round trip crossing the internet
  // is never a millisecond -- so the fast buckets that matter inbound would all be empty here. Since this
  // is also the widest stream we publish, spending them costs the most.
  private static readonly double[] ourUpstreamLatencyBoundaries = [0.1, 1, 10];

  // Connections live on a different scale -- PooledConnectionLifetime caps them at ten minutes -- and the
  // only question worth bucketing is whether they reach that cap or are torn down early.
  private static readonly double[] ourConnectionLifetimeBoundaries = [10, 600];

  // No boundaries leaves a single +Inf bucket, so the stream degenerates to sum and count: 3 samples per
  // series. Used where the distribution is not something we would act on.
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
    // Inbound duration sits at 15 of Prometheus's 16 labels, so it is one tag away from the
    // same scrape failure the client metrics caused: ASP.NET Core adds http.route once an
    // endpoint matches and error.type on unhandled exceptions. Pre-emptively drop the tags
    // that carry no signal here -- url.scheme is always http behind the ingress, the protocol
    // version is uniformly 1.1, and this proxy serves everything from catch-all middleware so
    // http.route is "(missing)" rather than a real route.
    .AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration
    {
      TagKeys =
      ["http.request.method", "http.response.status_code", "error.type", "aspnetcore.request.is_unhandled"],
      Boundaries = ourInboundLatencyBoundaries
    })
    // Authentication timing is a yes/no health signal (the scheme either validates tokens or it does
    // not), so keep the rate and the mean and drop the distribution.
    .AddView("aspnetcore.authentication.authenticate.duration", new ExplicitBucketHistogramConfiguration
    {
      Boundaries = ourCountAndSumOnly
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
    // http.request.method is dropped here, unlike on the inbound metric: it is a plain multiplier on the
    // widest stream we publish (upstream x method x outcome), and the proxy mirrors the inbound verb
    // upstream, so http.server.request.duration already carries it. What outbound diagnosis needs is which
    // upstream produced which outcome.
    .AddView("http.client.request.duration", new ExplicitBucketHistogramConfiguration
    {
      TagKeys = ["server.address", "http.response.status_code", "error.type"],
      Boundaries = ourUpstreamLatencyBoundaries
    })
    // Queue time answers "is MaxConnectionsPerServer throttling this upstream", and the remedy is the
    // same whatever the shape of the wait, so the rate and the mean are enough.
    .AddView("http.client.request.time_in_queue", new ExplicitBucketHistogramConfiguration
    {
      TagKeys = ["server.address"],
      Boundaries = ourCountAndSumOnly
    })
    .AddView("http.client.connection_duration", new ExplicitBucketHistogramConfiguration
    {
      TagKeys = ["server.address"],
      Boundaries = ourConnectionLifetimeBoundaries
    })
    .AddView("http.client.open_connections", new MetricStreamConfiguration
    {
      TagKeys = ["server.address", "http.connection.state"]
    })
    // dns.question.name is the upstream host, so it is bounded by the configured remote list and worth
    // keeping -- but which resolutions are slow matters far less than whether they fail.
    .AddView("dns.lookup.duration", new ExplicitBucketHistogramConfiguration
    {
      TagKeys = ["dns.question.name", "error.type"],
      Boundaries = ourCountAndSumOnly
    })
    .AddMeter(CachingProxyMetrics.MeterName);
}
