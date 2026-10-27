namespace JetBrains.CachingProxy;

/// <param name="DefaultNamespace">
/// The namespace to expand a single-segment repository name into, for an OCI prefix whose upstream keeps
/// its unqualified images in one (Docker Hub's <c>library</c>). See
/// <see cref="RemoteServers.RemoteServer.GetUpstreamUri"/> for what it does and why the upstream cannot be
/// left to do it. Null - no expansion - for every other prefix.
/// </param>
/// <param name="PublicUrl">
/// The URL clients reach this prefix's upstream root at, which a <see cref="CachingRule.RewriteUrls"/>
/// endpoint's JSON links are rewritten to. Null - no rewriting - by default.
/// </param>
public record CachingProxyPrefix(
  string Prefix,
  CacheDuration? CacheDuration = null,
  string? Profile = null,
  string? DefaultNamespace = null,
  bool? IsPrivate = null,
  string? PublicUrl = null)
{
  public static implicit operator CachingProxyPrefix(string prefix) => new(prefix);

  public override string ToString()
  {
    var result = Prefix;
    if (IsPrivate is {} isPrivate) result += $" {(isPrivate ? "🔒" : "🔓")}";
    if (CacheDuration != null) result += $" {CacheDuration}";
    if (Profile != null) result += $" [{Profile}]";
    if (DefaultNamespace != null) result += $" +{DefaultNamespace}/";
    if (PublicUrl != null) result += $" -> {PublicUrl}";
    return result;
  }
}
