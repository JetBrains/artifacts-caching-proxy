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
/// The tags served requests reported on <c>caching_requests</c>, for tests asserting what a real request
/// through the pipeline labels itself with. Test hosts wire no exporter (OpenTelemetry lives in
/// <c>Program.Main</c> alone, so there is no /metrics endpoint to scrape), which leaves a listener as the only
/// way in.
/// <para>Bound to the host's own <see cref="Meter"/> instance rather than to the meter name: the name is
/// process-wide, so every other proxy test host running at the same time publishes the same counter, and
/// their measurements would land here too.</para>
/// </summary>
internal sealed class RequestMetricRecorder : IDisposable
{
  private readonly MeterListener myListener = new();
  private readonly ConcurrentQueue<KeyValuePair<string, object?>[]> myMeasurements = new();

  public RequestMetricRecorder(IHost host)
  {
    var meter = host.Services.GetRequiredService<CachingProxyMetrics>().Meter;
    myListener.InstrumentPublished = (instrument, listener) =>
    {
      // Start() replays instruments already published, and CachingProxyMetrics declares this one in its
      // constructor, so the counter is picked up however long the host has been up.
      if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "caching_requests")
        listener.EnableMeasurementEvents(instrument);
    };
    myListener.SetMeasurementEventCallback<long>((_, _, tags, _) => myMeasurements.Enqueue(tags.ToArray()));
    myListener.Start();
  }

  /// <summary>
  /// One label's value per recorded request, in the order the requests were served. Single, so a label that
  /// was dropped or emitted twice fails the test rather than quietly reading as absent.
  /// </summary>
  public IEnumerable<string?> TagValues(string key) =>
    myMeasurements.Select(tags => Assert.Single(tags, tag => tag.Key == key).Value as string);

  public void Dispose() => myListener.Dispose();
}
