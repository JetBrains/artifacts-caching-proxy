using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace JetBrains.CachingProxy.Tests;

/// <summary>
/// Guards the two Prometheus scrape guardrails that took production down twice: the per-series
/// <c>label_limit</c> and the per-scrape <c>sample_limit</c>. Neither is visible at any single call site --
/// they are properties of the exposition as a whole -- so these tests scrape the real
/// <c>/metrics</c> endpoint through <see cref="MetricsConfiguration.ConfigureOurMetrics"/> and count what
/// Prometheus would actually count, rather than reasoning about views and boundaries.
/// <para>Run alone: a MeterProvider subscribes to meters by name, process-wide, so any other test host up
/// at the same time contributes its own series to this scrape - most visibly the runtime's own
/// <c>Microsoft.AspNetCore.Authentication</c> meter, which every proxy test host creates. Counting samples
/// is only meaningful when nothing else is emitting them.</para>
/// </summary>
[Collection(nameof(MetricsConfigurationTest))]
[CollectionDefinition(nameof(MetricsConfigurationTest), DisableParallelization = true)]
public class MetricsConfigurationTest(ITestOutputHelper output)
{
  // Target labels Prometheus merges into every sample from the kubernetes-pods job (app, pod index,
  // instance, job, node name, namespace, version). __name__ counts too, and the exporter adds
  // otel_scope_name on top of whatever tags survive the views.
  private const int PrometheusLabelLimit = 16;
  private const int TargetLabelCount = 7;

  // The upstreams the proxy actually talks to, as observed in production. Series are cumulative and never
  // retire, so the exposition eventually holds one series per combination that has ever occurred.
  private static readonly string[] ourUpstreams =
  [
    "repo.maven.apache.org", "plugins.gradle.org", "repo.gradle.org", "plugins-artifacts.gradle.org",
    "maven-central.storage-download.googleapis.com", "dl.google.com", "clojars.org", "repo.clojars.org",
    "github.com", "release-assets.githubusercontent.com", "packages.jetbrains.team",
    "plugins.jetbrains.com", "downloads.marketplace.jetbrains.com", "redirector.kotlinlang.org",
    "s3.eu-west-1.amazonaws.com", "localhost"
  ];

  // Upstreams that answer with a redirect the proxy does not follow, so a 3xx really does reach the
  // client metric. Everywhere else AllowAutoRedirect swallows it and only the final status is recorded,
  // which is why saturating all 16 upstreams with all 3xx codes would be a fiction rather than a ceiling.
  private static readonly string[] ourRedirectingUpstreams =
    ["github.com", "redirector.kotlinlang.org", "downloads.marketplace.jetbrains.com", "dl.google.com"];

  // Outcomes any upstream can produce, whatever it serves.
  private static readonly string[] ourUniversalOutcomes = ["200", "404"];
  private static readonly string[] ourErrorTypes = ["connection_error", "System.Threading.Tasks.TaskCanceledException"];
  private static readonly string[] ourRedirectStatusCodes = ["301", "302", "307"];

  private static readonly string[] ourInboundMethods = ["GET", "HEAD", "POST"];
  private static readonly string[] ourInboundStatusCodes = ["200", "302", "400", "404", "500"];

  // The caching_requests label space at its worst case: every outcome the enum can report, on every shipped
  // profile plus the null a profile-less prefix reports, authenticated or not. All closed sets fixed at
  // startup, so this is the ceiling rather than a sample of it.
  private static readonly string?[] ourProfiles = ["maven", "npm", "docker", null];
  private static readonly bool[] ourAuthenticated = [true, false];

  // S3 mode, so CachingProxyMetrics leaves the disk gauges unregistered: they are covered by
  // CacheDiskMetricsTest and would only add samples the tests here say nothing about.
  private static CachingProxyConfig BucketModeConfig() =>
    new() { S3 = new CachingProxyConfig.S3Config("test-bucket") };

  [Theory]
  // Samples one series costs: one _bucket line per boundary plus +Inf, then _sum and _count. Pinned rather
  // than bounded, because they multiply against a series count we do not control.
  //
  // Instrument names come from the runtime's declarations, NOT from the views under test -- this test
  // supplies the name it records under, so it would agree with a typo'd view and pass while production
  // publishes an unshaped stream. EveryTrimmedStream_DropsTheTagsWeDoNotSliceBy is the name-independent
  // guard.
  [InlineData("System.Net.Http", "http.client.request.duration", "http_client_request_duration_seconds", 8)]
  [InlineData("Microsoft.AspNetCore.Hosting", "http.server.request.duration", "http_server_request_duration_seconds", 9)]
  [InlineData("System.Net.Http", "http.client.connection.duration", "http_client_connection_duration_seconds", 5)]
  [InlineData("System.Net.Http", "http.client.request.time_in_queue", "http_client_request_time_in_queue_seconds", 2)]
  [InlineData("System.Net.NameResolution", "dns.lookup.duration", "dns_lookup_duration_seconds", 2)]
  [InlineData("Microsoft.AspNetCore.Authentication", "aspnetcore.authentication.authenticate.duration",
    "aspnetcore_authentication_authenticate_duration_seconds", 2)]
  public async Task Histogram_CostsTheExpectedNumberOfSamplesPerSeries(
    string meterName, string instrument, string prometheusName, int expectedSamples)
  {
    using var scrape = await MetricsScrape.Of(meter =>
      meter(meterName).CreateHistogram<double>(instrument, "s").Record(0.5));

    Assert.Equal(expectedSamples, scrape.SamplesOf(prometheusName));
  }

  [Fact]
  public async Task ClientRequestDuration_KeepsOnlyTheTagsWeSliceBy()
  {
    using var scrape = await MetricsScrape.Of(meter =>
      // The full default tag set the SDK attaches, including the ones that broke label_limit.
      meter("System.Net.Http").CreateHistogram<double>("http.client.request.duration", "s").Record(0.5,
        new KeyValuePair<string, object?>("server.address", "repo.maven.apache.org"),
        new KeyValuePair<string, object?>("server.port", 443),
        new KeyValuePair<string, object?>("url.scheme", "https"),
        new KeyValuePair<string, object?>("http.request.method", "GET"),
        new KeyValuePair<string, object?>("http.response.status_code", 200),
        new KeyValuePair<string, object?>("network.protocol.version", "1.1"),
        new KeyValuePair<string, object?>("network.peer.address", "151.101.2.132")));

    var bucketLabels = scrape.LabelsOf("http_client_request_duration_seconds_bucket");
    Assert.Equal(
      ["http_response_status_code", "le", "otel_scope_name", "server_address"],
      bucketLabels.OrderBy(static l => l, StringComparer.Ordinal));

    // Prometheus applies label_limit after merging target labels and before metric_relabel_configs, so an
    // over-limit series cannot be rescued from the scrape-config side. __name__ is the +1.
    Assert.True(bucketLabels.Count + TargetLabelCount + 1 <= PrometheusLabelLimit,
      $"{bucketLabels.Count} own labels + {TargetLabelCount} target labels + __name__ exceeds label_limit");
  }

  /// <summary>
  /// The guard for a view that matches no instrument. Views are keyed by instrument name and go silently
  /// inert when it is wrong, and the exporter sanitizes dots to underscores, so
  /// <c>http.client.connection_duration</c> and the real <c>http.client.connection.duration</c> both surface
  /// as <c>http_client_connection_duration_seconds</c> -- indistinguishable in the exposition. That typo
  /// shipped.
  ///
  /// So this test compares no names: it records every trimmed instrument with the runtime's full tag set and
  /// asserts the dropped tags are gone. A view matching nothing leaves them behind, whatever it is called.
  /// </summary>
  [Fact]
  public async Task EveryTrimmedStream_DropsTheTagsWeDoNotSliceBy()
  {
    using var scrape = await MetricsScrape.Of(RecordWithFullDefaultTags);

    // Dropped as constant per upstream (port, scheme, protocol), unbounded (peer address), or "(missing)"
    // behind catch-all middleware (route). Each is dropped by some view, so none may survive anywhere.
    string[] droppedLabels =
    [
      "server_port", "url_scheme", "network_protocol_version", "network_protocol_name",
      "network_peer_address", "network_peer_port", "http_route", "url_template"
    ];

    // Scoped to the families a view trims: the active_requests gauges are published untouched and keep
    // their default tags legitimately, at one cheap sample per series.
    string[] trimmedFamilies =
    [
      "http_client_request_duration_seconds", "http_client_connection_duration_seconds",
      "http_client_request_time_in_queue_seconds", "http_client_open_connections",
      "dns_lookup_duration_seconds", "http_server_request_duration_seconds"
    ];

    var surviving = scrape.SampleLines
      .Where(line => trimmedFamilies.Any(family => line.StartsWith(family, StringComparison.Ordinal)))
      .Where(line => droppedLabels.Any(label => line.Contains(label + "=\"", StringComparison.Ordinal)))
      .Select(static line => line.Split('}')[0] + "}")
      .Distinct()
      .ToList();

    Assert.Empty(surviving);
  }

  /// <summary>
  /// Records one point on every instrument a view trims, carrying the complete tag set the runtime attaches
  /// to it. Only the views decide what survives, which is what makes an unmatched view visible.
  /// </summary>
  private static void RecordWithFullDefaultTags(Func<string, Meter> meter)
  {
    var http = meter("System.Net.Http");
    var dns = meter("System.Net.NameResolution");
    var hosting = meter("Microsoft.AspNetCore.Hosting");
    var authentication = meter("Microsoft.AspNetCore.Authentication");

    static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    KeyValuePair<string, object?>[] endpoint =
    [
      Tag("server.address", "repo.maven.apache.org"), Tag("server.port", 443), Tag("url.scheme", "https"),
      Tag("network.protocol.version", "1.1"), Tag("network.protocol.name", "http")
    ];
    KeyValuePair<string, object?>[] peer =
      [Tag("network.peer.address", "151.101.2.132"), Tag("network.peer.port", 443)];

    http.CreateHistogram<double>("http.client.request.duration", "s").Record(0.5,
    [
      .. endpoint, .. peer, Tag("http.request.method", "GET"), Tag("http.response.status_code", 200),
      Tag("url.template", "/{**path}")
    ]);
    http.CreateHistogram<double>("http.client.connection.duration", "s").Record(120, [.. endpoint, .. peer]);
    http.CreateHistogram<double>("http.client.request.time_in_queue", "s")
      .Record(0.001, [.. endpoint, Tag("http.request.method", "GET")]);
    http.CreateUpDownCounter<long>("http.client.open_connections")
      .Add(1, [.. endpoint, .. peer, Tag("http.connection.state", "active")]);

    dns.CreateHistogram<double>("dns.lookup.duration", "s")
      .Record(0.01, Tag("dns.question.name", "repo.maven.apache.org"));

    hosting.CreateHistogram<double>("http.server.request.duration", "s").Record(0.002,
      Tag("http.request.method", "GET"), Tag("http.response.status_code", 200), Tag("url.scheme", "http"),
      Tag("network.protocol.version", "1.1"), Tag("http.route", "(missing)"),
      Tag("aspnetcore.request.is_unhandled", true));

    authentication.CreateHistogram<double>("aspnetcore.authentication.authenticate.duration", "s")
      .Record(0.003, Tag("aspnetcore.authentication.scheme", "Bearer"));
  }

  /// <summary>
  /// The exported label names and values of <c>caching_requests_total</c>, pinned. Nothing else declares
  /// them: the call site passes literals, and alerts and dashboards are written against the sanitized names a
  /// scrape shows, so a rename there is invisible until a query silently returns nothing. Driven through the
  /// real <see cref="CachingProxyMetrics.IncrementRequests"/> rather than a counter this test declares, so
  /// what is asserted is what production emits - instrument name, label names and label values alike.
  /// </summary>
  [Fact]
  public async Task RequestCounter_ExportsTheLabelsQueriesAreWrittenAgainst()
  {
    using var scrape = await MetricsScrape.Of(meter =>
    {
      var metrics = new CachingProxyMetrics(new PlainMeterFactory(meter), BucketModeConfig());
      metrics.IncrementRequests(CachingProxyStatus.HIT, "maven", authenticated: true);
      metrics.IncrementRequests(CachingProxyStatus.MISS, null, authenticated: false);
    });

    var labels = scrape.LabelsOf("caching_requests_total");
    Assert.Equal(["authenticated", "otel_scope_name", "profile", "status"],
      labels.OrderBy(static l => l, StringComparer.Ordinal));

    // Asserted here too because this counter has no view to cap it, so the call site is the only thing
    // standing between it and label_limit.
    Assert.True(labels.Count + TargetLabelCount + 1 <= PrometheusLabelLimit,
      $"{labels.Count} own labels + {TargetLabelCount} target labels + __name__ exceeds label_limit");

    // The values, not just the keys. "none" is what a prefix with no profile reports - a null would export as
    // an empty label, which Prometheus reads as no label at all - and the flag is a lowercase string rather
    // than a boxed bool, which the exporter would render "True".
    Assert.Contains(@"status=""HIT""", SeriesWith(scrape, @"profile=""maven"""));
    Assert.Contains(@"authenticated=""true""", SeriesWith(scrape, @"profile=""maven"""));
    Assert.Contains(@"status=""MISS""", SeriesWith(scrape, @"profile=""none"""));
    Assert.Contains(@"authenticated=""false""", SeriesWith(scrape, @"profile=""none"""));

    static string SeriesWith(MetricsScrape scrape, string discriminator) =>
      Assert.Single(scrape.SampleLines,
        l => l.StartsWith("caching_requests_total{", StringComparison.Ordinal)
             && l.Contains(discriminator, StringComparison.Ordinal));
  }

  /// <summary>
  /// The same, for the byte counter that rides along with the request counter. Its exported name is what
  /// makes it queryable at all - the instrument is <c>caching_content_bytes</c> and the exporter appends
  /// <c>_total</c>, which no call site spells out - and its labels have to be the request counter's own, or
  /// bytes cannot be divided by requests to get an average object size.
  /// </summary>
  [Fact]
  public async Task ContentCounter_ExportsTheSameLabelsAsTheRequestCounter()
  {
    using var scrape = await MetricsScrape.Of(meter =>
    {
      var metrics = new CachingProxyMetrics(new PlainMeterFactory(meter), BucketModeConfig());
      metrics.IncrementRequests(CachingProxyStatus.HIT, "maven", authenticated: true, cachedContentLength: 4096);
      // The instrument's other entry point, for a transfer reporting what it delivered after the fact. It
      // has to land on the very same series, or bytes would split across two of them by how they happened
      // to be reported.
      metrics.AddContentBytes(CachingProxyStatus.HIT, "maven", authenticated: true, bytes: 512);
      // No length: a request that delivered no content must leave the byte counter alone rather than
      // contribute a zero, which would still create the series and read as "an object of size 0".
      metrics.IncrementRequests(CachingProxyStatus.NEGATIVE_MISS, "maven", authenticated: true);
    });

    Assert.Equal(scrape.LabelsOf("caching_requests_total"), scrape.LabelsOf("caching_content_bytes_total"));
    var series = Assert.Single(scrape.SampleLines,
      l => l.StartsWith("caching_content_bytes_total{", StringComparison.Ordinal));
    Assert.Contains(@"status=""HIT""", series);
    // Both reports summed onto that one series. The value is the first field after the labels, the
    // exposition putting a scrape timestamp after it.
    Assert.Equal("4608", series[(series.LastIndexOf('}') + 1)..].Trim().Split(' ')[0]);
  }

  /// <summary>
  /// Hands <see cref="CachingProxyMetrics"/> a plain <see cref="Meter"/>, which the scrape's provider
  /// subscribes to by name. A Meter from a real <see cref="IMeterFactory"/> carries that factory as its
  /// Scope, and a provider built from another DI container may ignore it - the counter would then be absent
  /// from the exposition and these tests would pin nothing. Disposal stays with the scrape, which tracks
  /// every Meter it creates.
  /// </summary>
  private sealed class PlainMeterFactory(Func<string, Meter> create) : IMeterFactory
  {
    public Meter Create(MeterOptions options) => create(options.Name);

    public void Dispose() { }
  }

  /// <summary>
  /// The regression test for the outage itself. Series never retire while the process lives, so a
  /// long-running pod converges on every combination it can produce: this drives that saturated state and
  /// asserts the exposition still fits one scrape. The same input cost 17 samples per series before this
  /// configuration existed, which is what tripped <c>sample_limit</c>.
  /// </summary>
  [Fact]
  public async Task SaturatedExposition_FitsInOneScrape()
  {
    // A self-imposed ceiling, well under any sample_limit a deployment is likely to set. The real cap lives
    // in someone else's scrape config and is not ours to raise, so this is the one number we control. Raised
    // once, when caching_requests gained its profile and authenticated dimensions: 72 series of one sample.
    // Raised again for caching_content_bytes, which repeats that cross-product for another 72.
    const int sampleBudget = 1200;

    using var scrape = await MetricsScrape.Of(meter =>
    {
      var http = meter("System.Net.Http");
      var dns = meter("System.Net.NameResolution");
      var hosting = meter("Microsoft.AspNetCore.Hosting");

      var duration = http.CreateHistogram<double>("http.client.request.duration", "s");
      var queueTime = http.CreateHistogram<double>("http.client.request.time_in_queue", "s");
      var connection = http.CreateHistogram<double>("http.client.connection.duration", "s");
      var openConnections = http.CreateUpDownCounter<long>("http.client.open_connections");
      var lookup = dns.CreateHistogram<double>("dns.lookup.duration", "s");
      var inbound = hosting.CreateHistogram<double>("http.server.request.duration", "s");
      var requests = new CachingProxyMetrics(new PlainMeterFactory(meter), BucketModeConfig());

      foreach (var upstream in ourUpstreams)
      {
        var address = new KeyValuePair<string, object?>("server.address", upstream);
        queueTime.Record(0.001, address);
        lookup.Record(0.01, new KeyValuePair<string, object?>("dns.question.name", upstream));

        // Connection duration is the one stream the runtime tags with the peer IP, and CDN edge IPs rotate:
        // production saw several times more peers than upstreams within minutes of startup. Drive a few per
        // upstream so the count below reflects the view collapsing them onto server.address.
        foreach (var peer in (string[]) ["151.101.2.132", "151.101.66.132", "151.101.130.132"])
          connection.Record(120, address, new KeyValuePair<string, object?>("network.peer.address", peer));

        foreach (var state in (string[]) ["active", "idle"])
          openConnections.Add(1, address, new KeyValuePair<string, object?>("http.connection.state", state));

        foreach (var status in ourUniversalOutcomes)
          duration.Record(0.5, address, new KeyValuePair<string, object?>("http.response.status_code", status));

        // Failure paths carry no status code, so they fork their own series.
        foreach (var error in ourErrorTypes)
          duration.Record(0.5, address, new KeyValuePair<string, object?>("error.type", error));

        if (ourRedirectingUpstreams.Contains(upstream))
          foreach (var status in ourRedirectStatusCodes)
            duration.Record(0.5, address, new KeyValuePair<string, object?>("http.response.status_code", status));
      }

      foreach (var method in ourInboundMethods)
      foreach (var status in ourInboundStatusCodes)
        inbound.Record(0.002,
          new KeyValuePair<string, object?>("http.request.method", method),
          new KeyValuePair<string, object?>("http.response.status_code", status));

      // With a content length throughout, so the byte counter saturates the same cross-product rather
       // than staying absent from the scrape it has to fit in.
      foreach (var status in Enum.GetValues<CachingProxyStatus>())
      foreach (var profile in ourProfiles)
      foreach (var authenticated in ourAuthenticated)
        requests.IncrementRequests(status, profile, authenticated, cachedContentLength: 1024);
    });

    output.WriteLine($"total samples: {scrape.TotalSamples}");
    foreach (var (name, count) in scrape.SamplesByMetric.OrderByDescending(static p => p.Value))
      output.WriteLine($"  {count,5}  {name}");

    // Non-vacuous: a counter the provider never subscribed to would simply be absent, lowering the total and
    // passing. One sample per series for a counter, so this is the cross-product exactly - and it fails if a
    // view ever starts collapsing one of the dimensions.
    var requestSeries = Enum.GetValues<CachingProxyStatus>().Length * ourProfiles.Length * ourAuthenticated.Length;
    Assert.Equal(requestSeries, scrape.SamplesOf("caching_requests_total"));
    // The byte counter doubles that cross-product: same tags, so same series count, one sample each.
    Assert.Equal(requestSeries, scrape.SamplesOf("caching_content_bytes_total"));

    Assert.True(scrape.TotalSamples <= sampleBudget,
      $"exposition would publish {scrape.TotalSamples} samples, over the {sampleBudget} budget");
  }

  /// <summary>
  /// A live /metrics scrape of the production metric configuration, reduced to the sample lines Prometheus
  /// would ingest. Meters are created inside the callback with the same names the real instrumentation
  /// uses, so the views under test match them exactly.
  /// </summary>
  private sealed class MetricsScrape : IDisposable
  {
    private readonly List<Meter> myMeters = [];
    private IHost myHost = null!;

    private string[] mySampleLines = [];

    public static async Task<MetricsScrape> Of(Action<Func<string, Meter>> record)
    {
      var scrape = new MetricsScrape();
      scrape.myHost = await new HostBuilder()
        .ConfigureWebHost(webHost => webHost
          .UseTestServer()
          .ConfigureServices(services => services
            .AddOpenTelemetry()
            .WithMetrics(metrics => metrics
              .ConfigureOurMetrics()
              .AddPrometheusExporter()))
          .Configure(app => app.UseOpenTelemetryPrometheusScrapingEndpoint()))
        .StartAsync();

      record(scrape.Meter);

      var exposition = await scrape.myHost.GetTestClient().GetStringAsync("/metrics");
      scrape.mySampleLines =
        [.. exposition.Split('\n').Select(static l => l.Trim()).Where(static l => l.Length > 0 && l[0] != '#')];
      return scrape;
    }

    private Meter Meter(string name)
    {
      var meter = new Meter(name);
      myMeters.Add(meter);
      return meter;
    }

    public IReadOnlyList<string> SampleLines => mySampleLines;

    public int TotalSamples => mySampleLines.Length;

    public Dictionary<string, int> SamplesByMetric => mySampleLines
      .GroupBy(static l => l.Split('{', ' ')[0])
      .ToDictionary(static g => g.Key, static g => g.Count());

    /// <summary>Sample lines belonging to one metric family, i.e. its _bucket, _sum and _count lines.</summary>
    public int SamplesOf(string prometheusName) =>
      mySampleLines.Count(l => l.StartsWith(prometheusName, StringComparison.Ordinal));

    /// <summary>Label names on the first sample line of the given series, in exposition (sanitized) form.</summary>
    public IReadOnlyList<string> LabelsOf(string prometheusName)
    {
      var line = Assert.Single(mySampleLines.Where(l => l.StartsWith(prometheusName + "{", StringComparison.Ordinal)).Take(1));
      var labels = line[(line.IndexOf('{') + 1)..line.LastIndexOf('}')];
      return [.. labels.Split(',').Select(static l => l.Split('=')[0].Trim())];
    }

    public void Dispose()
    {
      foreach (var meter in myMeters) meter.Dispose();
      myHost.Dispose();
    }
  }
}
