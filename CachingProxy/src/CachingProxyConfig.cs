using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JetBrains.CachingProxy;

public class CachingProxyConfig
{
  public record S3Config(string? BucketName = null, bool SignedLinks = false)
  {
    // Added to the S3 redirect cache duration so a presigned link stays valid slightly longer than the
    // cached redirect itself, avoiding a race where the link expires right as the redirect is replayed.
    public TimeSpan CacheOffsetDuration { get; init; } = TimeSpan.FromSeconds(5);

    // Objects whose body fits within this many bytes are probed in a single ranged GET and served
    // inline (200 + body, cached in L1/L2); larger objects are redirected to S3. Raising it trades
    // more probe bandwidth and cache footprint for fewer client round-trips on small/medium artifacts
    // (e.g. dependency metadata). Hosts have ample RAM headroom; the cost dimension is the L2 cache.
    public int InlineThresholdBytes { get; init; } = 32 * 1024;
  }

  public record RedisConfig(string? ConnectionString = null)
  {
    // Optional key prefix, useful when several apps share one Redis instance.
    public string? InstanceName { get; init; }
  }

  public CachingProxyPrefix[] Prefixes { get; init; } = [];

  // OAuth client-credentials auth for private upstreams, matched to each prefix by longest URL
  // prefix (see RemoteServers). Empty by default: upstreams without a matching entry are requested
  // unauthenticated, exactly as before.
  public UpstreamAuth[] UpstreamAuth { get; init; } = [];

  public S3Config? S3 { get; init; }
  public RedisConfig? Redis { get; init; }
  public string LocalCachePath { get; init; } = Path.Combine(Path.GetTempPath(), "artifacts-caching-proxy");
  public string? BlacklistUrlRegex { get; init; }
  public long MinimumFreeDiskSpaceMb { get; init; } = 2048;
  public long RequestTimeoutSec { get; init; } = 20;
  public string RedirectToRemoteUrlsRegex { get; init; } = $"^(.*-/npm/v1/security/.*|.*-SNAPSHOT.*|.*maven-metadata\\.xml({string.Join('|', CheckSumExtensions.Select(Regex.Escape))})?)$";

  public static readonly string[] CheckSumExtensions = [".sha1", ".sha256", ".sha512", ".md5"];

  public string? UserAgentComment { get; init; }

  public string? CleanupInterval { get; init; }
  public TimeSpan CleanupPeriod { get; init; } = TimeSpan.FromDays(7);

  public CacheDuration CacheDuration { get; init; } = new();

  // Global per-status TTLs for the L2 (distributed/Redis) cache, mirroring CacheDuration (same
  // defaults). Configured globally only, never per prefix. The effective L2 duration for a status
  // code is max(L1, L2): L2 is never shorter than L1, so the durable backing store never expires
  // before the in-process copy.
  public CacheDuration DistributedCacheDuration { get; init; } = new();
}
