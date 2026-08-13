using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace JetBrains.CachingProxy.Tests;

/// <summary>
/// The disk-usage instruments, at the levels they can be wrong at: the name an alert has to be written
/// against, and which measurements exist at all.
/// <para>None of it is asserted against the real meter's own exposition, because in-process that is not
/// attributable. A MeterProvider subscribes to meters by name across the whole process and duplicate
/// instrument names collapse into a single metric stream, so a scrape taken while any other test host is up
/// shows one line per name contributed by whichever host won the race - proving neither a value nor an
/// absence. Everything here binds to one object instead: a <see cref="MeterListener"/> on one
/// <see cref="Meter"/> instance, or a meter name only this test uses.</para>
/// </summary>
public class CacheDiskMetricsTest(ITestOutputHelper output) : IAsyncLifetime
{
  // The exposition contract, in the form the two halves of it can be checked against independently: the
  // runtime must declare exactly these instruments with exactly these units, and the exporter must turn that
  // pair into a Prometheus name identical to the instrument name. Neither test alone would catch a typo --
  // one supplies the names, the other supplies the units -- but a mismatch fails one of them.
  private static readonly (string Instrument, string Unit)[] ourDiskInstruments =
  [
    ("local_cache_disk_free_bytes", "bytes"),
    ("local_cache_disk_total_bytes", "bytes"),
    ("local_cache_disk_minimum_free_bytes", "bytes"),
    ("local_cache_size_bytes", "bytes"),
    ("local_cache_files", "files")
  ];

  private readonly List<IHost> myHosts = [];
  private readonly List<string> myTempDirectories = [];

  [Fact]
  public async Task DiskInstruments_AreDeclaredWithTheUnitsTheExpositionAssumes()
  {
    var config = new CachingProxyConfig
    {
      LocalCachePath = NewTempDirectory(),
      CleanupInterval = "0 0 * * *"
    };

    var metrics = NewMetrics(config);
    var cleanup = new CleanupService(new FakeTimeProvider(DateTimeOffset.UtcNow), config, metrics,
      NullLogger<CleanupService>.Instance);

    var declared = new List<(string, string?)>();
    using var listener = new MeterListener
    {
      InstrumentPublished = (instrument, _) =>
      {
        if (ReferenceEquals(instrument.Meter, metrics.Meter) &&
            instrument.Name.StartsWith("local_cache", StringComparison.Ordinal))
          declared.Add((instrument.Name, instrument.Unit));
      }
    };
    listener.Start();

    // The footprint pair is registered by the cleanup loop, which starts on its own schedule - StartAsync
    // returns before ExecuteAsync has run any of its body. So wait for the declarations rather than assuming
    // they are there; the listener keeps firing as instruments are published.
    await cleanup.StartAsync(CancellationToken.None);
    try
    {
      for (var i = 0; i < 40 && declared.Count < ourDiskInstruments.Length; i++)
        await Task.Delay(TimeSpan.FromMilliseconds(25));

      foreach (var (name, unit) in declared) output.WriteLine($"{name} [{unit}]");
      Assert.Equal(ourDiskInstruments.OrderBy(static i => i.Instrument, StringComparer.Ordinal),
        declared.Select(static d => (Instrument: d.Item1, Unit: d.Item2 ?? ""))
          .OrderBy(static d => d.Instrument, StringComparer.Ordinal));
    }
    finally
    {
      await cleanup.StopAsync(CancellationToken.None);
      cleanup.Dispose();
    }
  }

  /// <summary>
  /// The Prometheus names, read off a real scrape rather than assumed: the exporter sanitizes separators and
  /// appends the unit unless the name already carries it, so the name a query has to say is not automatically
  /// the name in the source. Declared on a meter only this test uses, so the exposition holds nothing else.
  /// </summary>
  [Fact]
  public async Task DeclaredInstruments_AreExposedUnderTheirOwnNames()
  {
    const string probeMeterName = $"{nameof(CacheDiskMetricsTest)}.NameProbe";
    using var probe = new Meter(probeMeterName);
    foreach (var (instrument, unit) in ourDiskInstruments)
      probe.CreateObservableGauge(instrument, static () => 1L, unit);

    var host = await StartHostAsync(services => services
      .AddOpenTelemetry()
      .WithMetrics(metrics => metrics.AddMeter(probeMeterName).AddPrometheusExporter()));

    var exposition = await host.GetTestClient().GetStringAsync("/metrics");
    output.WriteLine(exposition);

    var exposed = exposition.Split('\n')
      .Select(static l => l.Trim())
      .Where(static l => l.Length > 0 && l[0] != '#')
      .Select(static l => l.Split('{', ' ')[0])
      .Distinct()
      .OrderBy(static n => n, StringComparer.Ordinal);

    Assert.Equal(ourDiskInstruments.Select(static i => i.Instrument).OrderBy(static n => n, StringComparer.Ordinal),
      exposed);
  }

  /// <summary>
  /// A failed read measures nothing, not zero. Zero free bytes is a real and alarming value that a full
  /// volume genuinely reports, so an unreadable path must not be able to forge it - an unreadable cache path
  /// is the health check's signal to raise, and it does raise it.
  /// </summary>
  [Fact]
  public void VolumeGauges_MeasureTheCacheVolume_AndNothingWhenItCannotBeRead()
  {
    var readable = Measure(NewMetrics(new CachingProxyConfig
    {
      LocalCachePath = NewTempDirectory(),
      MinimumFreeDiskSpaceMb = 3072
    }));

    Assert.True(readable["local_cache_disk_free_bytes"] > 0);
    Assert.True(readable["local_cache_disk_total_bytes"] >= readable["local_cache_disk_free_bytes"]);
    Assert.Equal(3072L * 1024 * 1024, readable["local_cache_disk_minimum_free_bytes"]);

    var unreadable = Measure(NewMetrics(new CachingProxyConfig
    {
      LocalCachePath = "not-a-path: / ",
      MinimumFreeDiskSpaceMb = 64
    }));

    Assert.DoesNotContain("local_cache_disk_free_bytes", unreadable.Keys);
    Assert.DoesNotContain("local_cache_disk_total_bytes", unreadable.Keys);
    // The trip point still measures: it comes from config, not from the volume.
    Assert.Equal(64L * 1024 * 1024, unreadable["local_cache_disk_minimum_free_bytes"]);
  }

  /// <summary>
  /// A bucket-mode deployment has no cache volume, and no disk health check registered either, so the three
  /// volume gauges must not exist there at all. Absence has to be checked at declaration rather than at
  /// measurement: free and total fall silent on an unreadable path anyway, but the minimum is a plain config
  /// read that always succeeds, so left registered it would publish a trip point nothing enforces.
  /// </summary>
  [Fact]
  public void VolumeGauges_AreNotDeclaredInS3Mode()
  {
    // A readable cache path on purpose. What has to keep the gauges away is the mode, not a failing read -
    // with an unreadable path the interesting instrument would be missing for the uninteresting reason.
    var metrics = NewMetrics(new CachingProxyConfig
    {
      LocalCachePath = NewTempDirectory(),
      S3 = new CachingProxyConfig.S3Config("test-bucket")
    });

    // Start() replays the instruments this meter has already published, which is all of them: everything
    // CachingProxyMetrics declares, it declares in its constructor.
    var declared = new List<string>();
    using var listener = new MeterListener
    {
      InstrumentPublished = (instrument, _) =>
      {
        if (ReferenceEquals(instrument.Meter, metrics.Meter)) declared.Add(instrument.Name);
      }
    };
    listener.Start();

    foreach (var name in declared) output.WriteLine(name);
    Assert.DoesNotContain(declared, static n => n.StartsWith("local_cache", StringComparison.Ordinal));
    // And the guard reaches no further than the disk: the request counters are mode-independent.
    Assert.Contains("caching_requests", declared);
  }

  /// <summary>
  /// The footprint counts what survived the walk, not what the walk deleted - and before the first walk it
  /// measures nothing rather than zero, since an empty cache is what a fresh node really reports and the two
  /// would otherwise be indistinguishable.
  /// </summary>
  [Fact]
  public async Task CacheFootprintGauges_CountWhatTheCleanupWalkKept()
  {
    var cachePath = NewTempDirectory();
    Directory.CreateDirectory(Path.Combine(cachePath, "ab"));
    File.WriteAllBytes(Path.Combine(cachePath, "ab", "kept-1.jar"), new byte[1000]);
    File.WriteAllBytes(Path.Combine(cachePath, "ab", "kept-2.jar"), new byte[7000]);

    // Old enough to be past the cutoff, so the walk deletes it and it must not be counted.
    var expired = Path.Combine(cachePath, "ab", "expired.jar");
    File.WriteAllBytes(expired, new byte[500]);

    var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
    File.SetLastAccessTimeUtc(expired, timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(30));

    var config = new CachingProxyConfig
    {
      LocalCachePath = cachePath,
      CleanupInterval = "* * * * *",
      CleanupPeriod = TimeSpan.FromDays(1)
    };

    var metrics = NewMetrics(config);
    var cleanup = new CleanupService(timeProvider, config, metrics, NullLogger<CleanupService>.Instance);
    await cleanup.StartAsync(CancellationToken.None);
    try
    {
      var beforeFirstRun = Measure(metrics);
      Assert.DoesNotContain("local_cache_size_bytes", beforeFirstRun.Keys);
      Assert.DoesNotContain("local_cache_files", beforeFirstRun.Keys);

      // The loop waits on a fake-clock timer, so the walk only happens when the clock moves. Advance whole
      // cron periods and let the continuation run, polling rather than waiting a fixed amount: the first
      // occurrence is however much of the current minute is left.
      for (var attempt = 0; File.Exists(expired) && attempt < 10; attempt++)
      {
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await DrainAsync();
      }

      Assert.False(File.Exists(expired));

      var afterFirstRun = Measure(metrics);
      Assert.Equal(8000, afterFirstRun["local_cache_size_bytes"]);
      Assert.Equal(2, afterFirstRun["local_cache_files"]);
    }
    finally
    {
      await cleanup.StopAsync(CancellationToken.None);
      cleanup.Dispose();
    }
  }

  /// <summary>
  /// Every long-valued measurement one meter instance produces right now, keyed by instrument name. Bound to
  /// the instance rather than the meter name, so nothing another test host publishes can appear here. An
  /// instrument that measures nothing is simply absent.
  /// </summary>
  private Dictionary<string, long> Measure(CachingProxyMetrics metrics)
  {
    var measured = new Dictionary<string, long>();
    using var listener = new MeterListener
    {
      InstrumentPublished = (instrument, l) =>
      {
        if (ReferenceEquals(instrument.Meter, metrics.Meter)) l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measured[instrument.Name] = value);
    listener.Start();
    listener.RecordObservableInstruments();

    foreach (var (name, value) in measured.OrderBy(static p => p.Key, StringComparer.Ordinal))
      output.WriteLine($"{name} {value}");
    return measured;
  }

  private static CachingProxyMetrics NewMetrics(CachingProxyConfig config) => new ServiceCollection()
    .AddMetrics()
    .AddSingleton(config)
    .AddSingleton<CachingProxyMetrics>()
    .BuildServiceProvider()
    .GetRequiredService<CachingProxyMetrics>();

  private string NewTempDirectory()
  {
    var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(path);
    myTempDirectories.Add(path);
    return path;
  }

  private async Task<IHost> StartHostAsync(Action<IServiceCollection> configure)
  {
    var host = await new HostBuilder()
      .ConfigureWebHost(webHost => webHost
        .UseTestServer()
        .ConfigureServices(configure)
        .Configure(app => app.UseOpenTelemetryPrometheusScrapingEndpoint()))
      .StartAsync();
    myHosts.Add(host);
    return host;
  }

  /// <summary>Yields repeatedly so a background loop's continuation can run after a fake-clock change.</summary>
  private static async Task DrainAsync()
  {
    for (var i = 0; i < 20; i++)
      await Task.Delay(TimeSpan.FromMilliseconds(25));
  }

  public Task InitializeAsync() => Task.CompletedTask;

  public async Task DisposeAsync()
  {
    foreach (var host in myHosts)
    {
      await host.StopAsync();
      host.Dispose();
    }

    foreach (var directory in myTempDirectories)
    {
      try
      {
        Directory.Delete(directory, true);
      }
      catch (Exception)
      {
        // A temp directory that will not go is the OS's problem, not the test's.
      }
    }
  }
}
