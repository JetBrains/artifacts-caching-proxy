using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;

namespace JetBrains.CachingProxy;

public class CachingProxyMetrics
{
  public static readonly string MeterName = typeof(CachingProxyMetrics).Namespace!;

  private readonly Counter<long> myRequestsCounter;
  private readonly Counter<long> myContentCounter;
  private readonly Counter<long> myRedirectSignatureCounter;

  public CachingProxyMetrics(IMeterFactory meterFactory, CachingProxyConfig config)
  {
    Meter = meterFactory.Create(MeterName);
    myRequestsCounter = Meter.CreateCounter<long>("caching_requests");
    // Content bytes delivered to clients: what this service wrote to a response body, whether it came off
    // a stored copy or straight from an upstream on its way into the cache, plus what a redirected client
    // is sent to fetch from the bucket. Bodies only - a HEAD, a 304 or an error head delivers none.
    myContentCounter = Meter.CreateCounter<long>("caching_content_bytes");
    myRedirectSignatureCounter = Meter.CreateCounter<long>("caching_redirect_signature_verifications");

    // Headroom on the volume the cache writes to. This is what actually runs out: in disk mode the cache
    // is a host path shared with whatever else the node runs, and eviction is driven by
    // CachingProxy.HealthCheck taking the process out of service, not by a quota. Three series, no tags.
    //
    // Free is the number that moves. Total is published so a dashboard can show a ratio without pinning
    // node size into the query, and it re-reads per scrape rather than being captured once because a
    // resized volume must not keep reporting the old size. The minimum is the health check's own trip
    // point, so an alert reads `free < minimum` and keeps agreeing with the health check after the knob
    // moves.
    //
    // Disk mode only, matching where Program registers CleanupService and that health check. A bucket-mode
    // deployment has no volume to read, so free and total would fall silent there anyway - but the minimum
    // is a plain config read that cannot fail, and alone it would assert a trip point nothing enforces.
    if (!config.IsS3Mode)
    {
      Meter.CreateObservableGauge("local_cache_disk_free_bytes",
        () => ObserveVolume(config.LocalCachePath, static drive => drive.AvailableFreeSpace),
        "bytes", "Free bytes on the volume holding the local cache");

      Meter.CreateObservableGauge("local_cache_disk_total_bytes",
        () => ObserveVolume(config.LocalCachePath, static drive => drive.TotalSize),
        "bytes", "Size of the volume holding the local cache");

      Meter.CreateObservableGauge("local_cache_disk_minimum_free_bytes",
        () => config.MinimumFreeDiskSpaceMb * 1024 * 1024,
        "bytes", "Free bytes below which the health check reports unhealthy");
    }
  }

  /// <summary>
  /// Reads one field of the cache volume, publishing nothing if the read fails. Nothing rather than zero:
  /// zero free bytes is a real and alarming value, so a failed statfs must not be able to forge it. An
  /// unreadable cache path is already a health-check failure, which is the signal that belongs to it.
  /// </summary>
  private static IEnumerable<Measurement<long>> ObserveVolume(string path, Func<DriveInfo, long> read)
  {
    long value;
    try
    {
      value = read(new DriveInfo(path));
    }
    catch (Exception)
    {
      yield break;
    }

    yield return new Measurement<long>(value);
  }

  public Meter Meter { get; }

  /// <summary>
  /// One served request, sliced by outcome, by the caching profile that decided how it was cached, and by
  /// whether the caller presented a credential we accepted.
  /// <para>All three are closed sets fixed at startup - outcomes by the enum, profiles by the configuration -
  /// so the series count cannot grow with traffic, only with a config change. That is why this counter needs
  /// no trimming view, unlike the http.client.* streams in <see cref="MetricsConfiguration"/>.</para>
  /// </summary>
  /// <param name="status">The outcome the request was served with.</param>
  /// <param name="profile">The CachingProfiles key, or null for a prefix that declares none.</param>
  /// <param name="authenticated">Whether the inbound request carried a credential we validated.</param>
  /// <param name="cachedContentLength">The content this response delivers, when the head already knows the
  /// figure: a body it carries in full, or the object a redirect sends the client to fetch. Null when it
  /// delivers none (a HEAD, an error) or when only the transfer will know how much went out, which reports
  /// it through <see cref="AddContentBytes"/>. Reported on a second counter carrying the same tags, so
  /// bytes served can be read by status, profile and inbound auth exactly as requests are.</param>
  public void IncrementRequests(CachingProxyStatus status, string? profile, bool authenticated, long? cachedContentLength = null)
  {
    var tagList = TagsFor(status, profile, authenticated);
    myRequestsCounter.Add(1, tagList);
    if (cachedContentLength.HasValue)
      myContentCounter.Add(cachedContentLength.Value, tagList);
  }

  /// <summary>
  /// The content bytes a response turned out to deliver, for the transfers whose head could not say: a body
  /// relayed from an upstream (a chunked one declares no length at all), and a stored copy served through a
  /// framework that answers conditional and ranged requests itself, sending a part of the file or none of
  /// it. Counts no request of its own - the head already counted one - so the two counters stay
  /// one-to-one on requests and differ only in bytes.
  /// </summary>
  public void AddContentBytes(CachingProxyStatus status, string? profile, bool authenticated, long bytes) =>
    myContentCounter.Add(bytes, TagsFor(status, profile, authenticated));

  private static TagList TagsFor(CachingProxyStatus status, string? profile, bool authenticated) =>
    new(
      // Literals, not nameof(...): the exported label name is what queries and alerts are written against,
      // so a parameter rename must not be able to rename it. Pinned in MetricsConfigurationTest.
      new KeyValuePair<string, object?>("status", status.ToString()),
      // "none" rather than an absent label, matching IncrementRedirectSignatureVerification below: an empty
      // value is "no label" to Prometheus, so `sum by (profile)` would lose the profile-less prefixes.
      new KeyValuePair<string, object?>("profile", profile ?? "none"),
      // A string, not the bool: boxing allocates on every request, and the exporter renders a tag value via
      // ToString(), so a bool would export as "True"/"False" instead of the conventional lower case.
      new KeyValuePair<string, object?>("authenticated", authenticated ? "true" : "false"));

  /// <summary>
  /// Records which key in the rotation ring validated a redirect signature (see
  /// <see cref="RedirectSignatureKeyRing"/>). This is what makes a rotation finishable: the retiring key
  /// may only be dropped once <c>key_role="retiring"</c> stops appearing, and a <c>"mismatch"</c> spike
  /// means the signer moved to a key this side does not hold. Cardinality is bounded by the ring size.
  /// </summary>
  /// <param name="keyRole">"active", "retiring", or "mismatch".</param>
  /// <param name="keyFingerprint">Fingerprint of the matched key, or "none" when nothing matched.</param>
  public void IncrementRedirectSignatureVerification(string keyRole, string keyFingerprint)
  {
    myRedirectSignatureCounter.Add(1,
      new KeyValuePair<string, object?>("key_role", keyRole),
      new KeyValuePair<string, object?>("key_fingerprint", keyFingerprint));
  }
}
