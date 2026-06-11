using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JetBrains.CachingProxy;

public class CachingProxyConfig
{
  public record S3Config(string? BucketName = null, bool SignedLinks = false);

  public CachingProxyPrefix[] Prefixes { get; init; } = [];
  public S3Config? S3 { get; init; }
  public string LocalCachePath { get; init; } = Path.Combine(Path.GetTempPath(), "artifacts-caching-proxy");
  public string? BlacklistUrlRegex { get; init; }
  public long MinimumFreeDiskSpaceMb { get; init; } = 2048;
  public long RequestTimeoutSec { get; init; } = 20;
  public string RedirectToRemoteUrlsRegex { get; init; } = $"^(.*-/npm/v1/security/audits/quick|.*-SNAPSHOT.*|.*maven-metadata\\.xml({string.Join('|', CheckSumExtensions.Select(Regex.Escape))})?)$";

  public static readonly string[] CheckSumExtensions = [".sha1", ".sha256", ".sha512", ".md5"];

  public string? UserAgentComment { get; init; }

  public string? CleanupInterval { get; init; }
  public TimeSpan CleanupPeriod { get; init; } = TimeSpan.FromDays(7);
}
