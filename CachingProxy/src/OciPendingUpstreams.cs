using System;
using System.Collections.Concurrent;

namespace JetBrains.CachingProxy;

/// <summary>
/// The OCI objects whose upstream has stopped answering in time, and since when.
/// <para>A pull-through registry answers a blob it has not cached yet only once it holds the whole blob: GAR's remote
/// over Space took 80 s for a 3 GB layer (MRI-5989). It keeps filling after we give up, so asking again works, but
/// Docker 29 and BuildKit wait at most 30 s for a response head and do not retry a failed layer, and a 404 is fatal
/// to every client. So a timed-out OCI request is answered with a redirect to itself instead of a cached 404 (see
/// <see cref="RemoteProxy.RedirectToSelfAsync"/>), and the client's own header timer restarts on each hop.</para>
/// <para>The loop is bounded per object, not per client: Docker 26 follows any number of redirects. Once an object
/// has been pending for <see cref="Limit"/>, its next timeout is negative-cached as before, so an upstream that is
/// stuck fails clearly instead of being asked again forever.</para>
/// <para>Per instance, on the domain clock: the bound needs to be roughly right, not shared.</para>
/// </summary>
public sealed class OciPendingUpstreams(TimeProvider timeProvider)
{
  // 20 GB at the slowest fill measured (34 MB/s); the largest layer ultimate pulls is 3.15 GB.
  public static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

  // Entries are dropped when their object resolves or runs out of time; this only bounds objects nobody asks for
  // again.
  private const int SweepThreshold = 1024;

  private readonly ConcurrentDictionary<string, DateTimeOffset> myPendingSince = new();

  /// <summary>
  /// Records a timeout for <paramref name="key"/>. True while the object has been pending for less than
  /// <see cref="Limit"/>; false once it has been longer, which also forgets it, so a later timeout starts over.
  /// </summary>
  public bool KeepWaiting(string key)
  {
    var now = timeProvider.GetUtcNow();
    if (myPendingSince.Count > SweepThreshold)
      foreach (var (staleKey, since) in myPendingSince)
        if (now - since >= Limit)
          myPendingSince.TryRemove(staleKey, out _);

    if (now - myPendingSince.GetOrAdd(key, now) < Limit)
      return true;

    myPendingSince.TryRemove(key, out _);
    return false;
  }

  /// <summary>The upstream answered for <paramref name="key"/>: it is no longer pending.</summary>
  public void Resolved(string key) => myPendingSince.TryRemove(key, out _);
}
