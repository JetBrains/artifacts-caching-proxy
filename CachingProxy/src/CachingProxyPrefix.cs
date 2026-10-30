namespace JetBrains.CachingProxy;

/// <param name="DefaultNamespace">
/// The namespace to expand a single-segment repository name into, for an OCI prefix whose upstream keeps
/// its unqualified images in one (Docker Hub's <c>library</c>). See
/// <see cref="RemoteServers.RemoteServer.GetUpstreamUri"/> for what it does and why the upstream cannot be
/// left to do it. Null - no expansion - for every other prefix.
/// </param>
/// <param name="Retention">
/// How long this prefix's stored copies are kept (MRI-5689): one of the <see cref="RetentionPolicy"/> names
/// (1d, 3d, 30d, 180d, 1y); anything else fails startup. Null keeps the global behaviour - the disk's
/// <see cref="CachingProxyConfig.CleanupPeriod"/>, and on S3 untagged objects under the bucket's default rule.
/// </param>
public record CachingProxyPrefix(
  string Prefix,
  CacheDuration? CacheDuration = null,
  string? Profile = null,
  string? DefaultNamespace = null,
  bool? IsPrivate = null,
  string? Retention = null)
{
  public static implicit operator CachingProxyPrefix(string prefix) => new(prefix);

  public override string ToString()
  {
    var result = Prefix;
    if (IsPrivate is {} isPrivate) result += $" {(isPrivate ? "🔒" : "🔓")}";
    if (CacheDuration != null) result += $" {CacheDuration}";
    if (Profile != null) result += $" [{Profile}]";
    if (DefaultNamespace != null) result += $" +{DefaultNamespace}/";
    if (Retention != null) result += $" ⌛{Retention}";
    return result;
  }
}
