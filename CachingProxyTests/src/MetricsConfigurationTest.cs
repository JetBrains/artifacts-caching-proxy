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
/// </summary>
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

  [Theory]
  // Samples one series costs in the exposition: one _bucket line per boundary plus +Inf, then _sum and
  // _count. These are the numbers that multiply against a series count we do not control, so they are
  // pinned rather than merely bounded. Zero boundaries emits no bucket lines at all, leaving sum/count.
  [InlineData("System.Net.Http", "http.client.request.duration", "http_client_request_duration_seconds", 6)]
  [InlineData("Microsoft.AspNetCore.Hosting", "http.server.request.duration", "http_server_request_duration_seconds", 8)]
  [InlineData("System.Net.Http", "http.client.connection_duration", "http_client_connection_duration_seconds", 5)]
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
  /// The regression test for the outage itself. Streams here are cumulative and series never retire while
  /// the process lives, so a long-running pod converges on every combination it can produce -- this drives
  /// that saturated state and asserts the exposition still fits one scrape. The same input published 17
  /// samples per series before this configuration existed, which is what tripped <c>sample_limit</c>.
  /// </summary>
  [Fact]
  public async Task SaturatedExposition_FitsInOneScrape()
  {
    // Prometheus's sample_limit for the kubernetes-pods job. Not ours to raise: it guards every pod in
    // that job, so the exposition has to fit it rather than the other way round.
    const int sampleBudget = 1000;

    using var scrape = await MetricsScrape.Of(meter =>
    {
      var http = meter("System.Net.Http");
      var dns = meter("System.Net.NameResolution");
      var hosting = meter("Microsoft.AspNetCore.Hosting");

      var duration = http.CreateHistogram<double>("http.client.request.duration", "s");
      var queueTime = http.CreateHistogram<double>("http.client.request.time_in_queue", "s");
      var connection = http.CreateHistogram<double>("http.client.connection_duration", "s");
      var openConnections = http.CreateUpDownCounter<long>("http.client.open_connections");
      var lookup = dns.CreateHistogram<double>("dns.lookup.duration", "s");
      var inbound = hosting.CreateHistogram<double>("http.server.request.duration", "s");

      foreach (var upstream in ourUpstreams)
      {
        var address = new KeyValuePair<string, object?>("server.address", upstream);
        queueTime.Record(0.001, address);
        connection.Record(120, address);
        lookup.Record(0.01, new KeyValuePair<string, object?>("dns.question.name", upstream));

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
    });

    output.WriteLine($"total samples: {scrape.TotalSamples}");
    foreach (var (name, count) in scrape.SamplesByMetric.OrderByDescending(static p => p.Value))
      output.WriteLine($"  {count,5}  {name}");

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
