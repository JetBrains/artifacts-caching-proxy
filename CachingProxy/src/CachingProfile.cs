using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace JetBrains.CachingProxy;

/// <summary>
/// A caching profile: an ordered set of <see cref="CachingRule"/>s that decides, per request path,
/// how an endpoint is cached. Assigned to a prefix by name (see <see cref="CachingProxyConfig.CachingProfiles"/>
/// and <see cref="CachingProxyPrefix.Profile"/>). The first rule whose pattern matches wins; when no
/// rule matches the caller treats it as "cache forever, never redirect" — the immutable default.
/// Profiles let different endpoints of one repository use different caching timing (e.g. immutable
/// Maven coordinates cached for a year, mutable metadata revalidated hourly) and are meant to grow to
/// other repository types (nuget v3, docker) over time.
/// </summary>
public class CachingProfile
{
  public CachingRule[] Rules { get; init; } = [];

  // First matching rule, or null when none matches.
  public CachingRule? Match(string path) => Rules.FirstOrDefault(rule => rule.IsMatch(path));

  // Compile every rule's regex once, up front, so a bad pattern fails at startup (in RemoteServers)
  // rather than on the first matching request. Returns this for fluent use.
  internal CachingProfile Compile()
  {
    foreach (var rule in Rules) rule.Compile();
    return this;
  }
}

/// <summary>
/// One rule of a <see cref="CachingProfile"/>: a request-path pattern mapped to a caching behavior.
/// A matched path is either always redirected to the upstream (<see cref="Redirect"/>) or cached and
/// served with the given freshness window (<see cref="RefreshAfter"/>).
/// </summary>
public class CachingRule
{
  public required string Pattern { get; init; }

  /// <summary>
  /// Freshness window of a cached artifact: once a stored copy is older than this it is revalidated
  /// against the upstream on the next access (a conditional GET — see
  /// <see cref="RemoteProxy.RevalidateAsync"/>). <c>null</c> means never revalidate (cached forever),
  /// and the client is told <c>max-age = 365 days</c>. Ignored when <see cref="Redirect"/> is true.
  /// </summary>
  public TimeSpan? RefreshAfter { get; init; }

  /// <summary>
  /// When true the matched path is always 307-redirected to the upstream and never cached (for
  /// dynamic, non-cacheable endpoints). <see cref="RefreshAfter"/> is ignored.
  /// </summary>
  public bool Redirect { get; init; }

  // Compiled lazily and cached; warmed up by CachingProfile.Compile at startup. RegexOptions.Compiled
  // matches the other request-path regexes in this codebase (see RemoteProxy).
  private Regex? myRegex;

  internal void Compile() => myRegex ??= new Regex(Pattern, RegexOptions.Compiled);

  public bool IsMatch(string path) => (myRegex ??= new Regex(Pattern, RegexOptions.Compiled)).IsMatch(path);
}
