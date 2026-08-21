using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

/// <summary>
/// What served requests reported on the two counters <see cref="CachingProxyMetrics.IncrementRequests"/>
/// writes -- <c>caching_requests</c> and the content bytes on <c>caching_content_bytes</c> -- for tests
/// asserting what a real request through the pipeline labels itself with. Test hosts wire no exporter
/// (OpenTelemetry lives in <c>Program.Main</c> alone, so there is no /metrics endpoint to scrape), which
/// leaves a listener as the only way in.
/// <para>Bound to the host's own <see cref="Meter"/> instance rather than to the meter name: the name is
/// process-wide, so every other proxy test host running at the same time publishes the same counters, and
/// their measurements would land here too.</para>
/// </summary>
internal sealed class RequestMetricRecorder : IDisposable
{
  private const string RequestsCounter = "caching_requests";
  private const string ContentCounter = "caching_content_bytes";

  private readonly MeterListener myListener = new();
  private readonly ConcurrentQueue<Recorded> myMeasurements = new();

  private sealed record Recorded(string Instrument, long Value, KeyValuePair<string, object?>[] Tags);

  public RequestMetricRecorder(IHost host) : this(host.Services) { }

  public RequestMetricRecorder(IServiceProvider services)
  {
    var meter = services.GetRequiredService<CachingProxyMetrics>().Meter;
    myListener.InstrumentPublished = (instrument, listener) =>
    {
      // Start() replays instruments already published, and CachingProxyMetrics declares these in its
      // constructor, so the counters are picked up however long the host has been up.
      if (ReferenceEquals(instrument.Meter, meter) && instrument.Name is RequestsCounter or ContentCounter)
        listener.EnableMeasurementEvents(instrument);
    };
    myListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
      myMeasurements.Enqueue(new Recorded(instrument.Name, value, tags.ToArray())));
    myListener.Start();
  }

  /// <summary>
  /// One label's value per recorded request, in the order the requests were served. Single, so a label that
  /// was dropped or emitted twice fails the test rather than quietly reading as absent.
  /// </summary>
  public IEnumerable<string?> TagValues(string key) =>
    Measurements(RequestsCounter).Select(m => TagValue(m, key));

  /// <summary>
  /// The status and byte count of every content measurement, in order. Bytes are reported only by a request
  /// that delivered content, so one that delivered none is absent here while still counted on
  /// <c>caching_requests</c> -- which is what makes a response counting a body it never sent visible.
  /// </summary>
  public IEnumerable<(string? Status, long Bytes)> ContentBytes =>
    Measurements(ContentCounter).Select(m => (TagValue(m, "status"), m.Value));

  /// <summary>The tags of the content measurements, so they can be compared against the request ones.</summary>
  public IEnumerable<string?> ContentTagValues(string key) =>
    Measurements(ContentCounter).Select(m => TagValue(m, key));

  private IEnumerable<Recorded> Measurements(string instrument) =>
    myMeasurements.Where(m => m.Instrument == instrument);

  private static string? TagValue(Recorded measurement, string key) =>
    Assert.Single(measurement.Tags, tag => tag.Key == key).Value as string;

  public void Dispose() => myListener.Dispose();
}
