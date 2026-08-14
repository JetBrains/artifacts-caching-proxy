using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy;

public class RemoteServers : EndpointDataSource
{
  public RemoteServers(CachingProxyConfig config, ILogger<RemoteServers> logger)
  {
    ValidateUpstreamAuth(config.UpstreamAuth);

    var endpoints = new Endpoint[config.Prefixes.Length];
    var hasGatedOciPrefix = false;
    logger.LogInformation("Creating {Count} endpoints", config.Prefixes.Length);
    for (var i = 0; i < config.Prefixes.Length; i++)
    {
      var prefix = config.Prefixes[i];
      var trimmed = prefix.Prefix.Trim('/');
      if (trimmed.Length == 0) throw new ArgumentException("Prefix is empty: " + prefix);

      var index = trimmed.IndexOf('=');
      var trimmedPrefix = index < 0 ? $"/{trimmed}" : $"/{trimmed[..index]}";

      var target = index < 0 ? trimmed : trimmed[(index + 1)..];
      target = target.TrimEnd('/') + '/';
      var remoteUri = Uri.TryCreate(target, UriKind.Absolute, out var targetUri) ? targetUri :
        new Uri(Uri.UriSchemeHttps + Uri.SchemeDelimiter + target, UriKind.Absolute);
      // A query or a fragment leaves the base with no trailing-slash path to resolve a request against
      // ("host/p?x=1" parses as path "/p" plus query), so every request to such a prefix would resolve
      // outside it and be rejected. Fail at startup rather than on every request.
      if (remoteUri.Query.Length > 0 || remoteUri.Fragment.Length > 0)
        throw new ArgumentException($"Prefix target must have no query or fragment: {prefix}");
      var matched = MatchAuth(remoteUri, config.UpstreamAuth);
      var matchedAuth = matched?.Value;
      if (matchedAuth != null && !remoteUri.IsSecureOrLoopback())
        throw new ArgumentException(
          $"Authenticated upstream '{remoteUri}' must use HTTPS except on loopback.");
      var remoteServer = new RemoteServer(trimmedPrefix, remoteUri,
        config.CacheDuration.Union(prefix.CacheDuration), matchedAuth,
        ResolveProfile(prefix.Profile, config.CachingProfiles));

      logger.LogInformation("RemoteServer: {Prefix} -> {RemoteUri}, Auth: {Auth}, Profile: {Profile}",
        remoteServer.Prefix, remoteServer.RemoteUri, remoteServer.Auth, prefix.Profile);
      // A matched entry with no credential is what a half-configured secret looks like: the prefix is gated
      // inbound, so it looks configured, while the upstream is reached anonymously - and an OCI upstream
      // mints its registry token anonymously too, which a private repository answers with a bare 403.
      if (matchedAuth is { HasCredential: false })
        logger.LogWarning(Event.IncompleteUpstreamAuth,
          "UpstreamAuth '{Name}' matched {Prefix} but has no credential to send, so {RemoteUri} is reached anonymously",
          matched!.Value.Key, remoteServer.Prefix, remoteServer.RemoteUri);
      // A prefix with a matched UpstreamAuth fetches with a credential of ours, so its inbound route must
      // require a validated client JWT too: attach an AuthorizeAttribute (enforced by
      // UseAuthentication/UseAuthorization). No exception for a credential that only buys rate limit on a
      // public registry - what an upstream grants for it is not visible from here, and the expensive
      // mistake is serving private artifacts to anyone. A prefix with no matched auth is fetched
      // anonymously and stays un-gated.
      var metadata = remoteServer.Auth != null ?
        new EndpointMetadataCollection(remoteServer, new AuthorizeAttribute()) :
        new EndpointMetadataCollection(remoteServer);
      hasGatedOciPrefix |= remoteServer.Auth != null && remoteServer.Profile?.Oci == true;

      // Overlapping prefixes (e.g. "/aprefix" and "/aprefix/too") both match via their {**path}
      // catch-all, and routing breaks such ties by Endpoint.Order, NOT by specificity. So order by
      // descending prefix length here, making the longer (more specific) prefix win regardless of the
      // order it was declared in. Equal-length prefixes can never match the same path, so ties between
      // them are never observed.
      endpoints[i] = new RouteEndpoint(
        requestDelegate: static _ => Task.CompletedTask,
        routePattern: RoutePatternFactory.Parse(trimmedPrefix + $"/{{**{PathParameterName}}}"),
        order: 0, // Yes, the same order for everything. Real order will be determined by the ASP.NET in runtime according to prefixes topology.
        metadata: metadata,
        displayName: $"Metadata-only {prefix}");
    }

    Endpoints = endpoints;
    HasGatedOciPrefix = hasGatedOciPrefix;
  }

  /// <summary>
  /// True when at least one OCI prefix carries an AuthorizeAttribute. A registry client picks its auth
  /// strategy for the whole host from the <c>GET /v2/</c> probe, so that probe has to challenge for such a
  /// deployment - see UseOciPing. Deployments whose OCI prefixes are all public keep an unchallenged probe.
  /// </summary>
  public bool HasGatedOciPrefix { get; }

  private const string PathParameterName = "path";

  // Resolve a prefix's profile name to its (compiled) CachingProfile. A missing name means no profile
  // (everything cached forever); a name that does not exist in the config is a misconfiguration and
  // fails fast at startup.
  private static CachingProfile? ResolveProfile(string? name, Dictionary<string, CachingProfile> profiles)
  {
    if (string.IsNullOrEmpty(name)) return null;
    if (!profiles.TryGetValue(name, out var profile))
      throw new ArgumentException($"Unknown caching profile '{name}'. Defined profiles: {string.Join(", ", profiles.Keys)}");
    return profile.Compile();
  }

  // An entry with no UrlPrefixes can never match an upstream, so every prefix it was meant to gate would be
  // left un-gated and fetched anonymously - and whatever those prefixes already hold in the cache would be
  // served to anyone. That is a half-configured secret failing open, so refuse to start, naming the entry so
  // the missing setting (UpstreamAuth__<name>__UrlPrefixes__0) is obvious.
  private static void ValidateUpstreamAuth(Dictionary<string, UpstreamAuth> auths)
  {
    foreach (var (name, auth) in auths)
      if (auth.UrlPrefixes.Length == 0)
        throw new ArgumentException(
          $"UpstreamAuth '{name}' has no UrlPrefixes, so it would gate nothing and leave its upstreams anonymous.");
  }

  // Among the auth entries whose UrlPrefixes contain a prefix of the upstream URL, the longest (most
  // specific) one wins, so a host-wide block and a path-scoped block can coexist. Returns null when
  // nothing matches, leaving the upstream unauthenticated. The entry keeps its configuration name, which
  // is what the credential-less warning has to point at (UpstreamAuth__<name>__…) to be actionable.
  private static KeyValuePair<string, UpstreamAuth>? MatchAuth(
    Uri remoteUri, IReadOnlyDictionary<string, UpstreamAuth> auths)
  {
    var remotePrefix = remoteUri.GetHostPortPath();
    return auths.
      SelectMany(entry => entry.Value.UrlPrefixes.Select(prefix => (UrlPrefix: prefix, Entry: entry)))
      .Where(match => remotePrefix.StartsWith(match.UrlPrefix, StringComparison.OrdinalIgnoreCase))
      .OrderByDescending(match => match.UrlPrefix.Length)
      .Select(match => (KeyValuePair<string, UpstreamAuth>?)match.Entry)
      .FirstOrDefault();
  }

  public static RemoteServer? GetRemoteServer(HttpContext context, out string? path)
  {
    path = context.GetRouteValue(PathParameterName)?.ToString();
    return context.GetEndpoint()?.Metadata.GetMetadata<RemoteServer>();
  }

  public record RemoteServer(PathString Prefix, Uri RemoteUri, CacheDuration CacheDuration, UpstreamAuth? Auth = null, CachingProfile? Profile = null)
  {
    /// <summary>
    /// The upstream URI for a request's <c>{**path}</c> remainder, or <c>null</c> when that remainder names
    /// something this prefix is not configured for - which the caller answers with a 400, see
    /// <see cref="RemoteProxy.ValidateRequestAsync"/>.
    /// <para>The remainder is an RFC-3986 reference resolved against <see cref="RemoteUri"/>, not a suffix
    /// appended to it: it can replace the base path ("/other"), the authority ("//host") or everything
    /// ("https://host/x"), and its dot segments can climb out of the base - including percent-encoded ones,
    /// which System.Uri unescapes and collapses after the request path was already checked. Everything
    /// derived from the result follows it out, while the inbound gate and the credential we send upstream
    /// belong to the prefix and are fixed at startup: an un-gated prefix could resolve onto a gated one's
    /// upstream path and serve its cache entries to anyone (MRI-4842), and a gated prefix could hand our own
    /// credential to a host of the caller's choosing (MRI-4844). So only a URI inside the base is returned.</para>
    /// </summary>
    public Uri? GetUpstreamUri(string? remainingPath)
    {
      if (string.IsNullOrEmpty(remainingPath)) return RemoteUri;

      // TryCreate, not the throwing constructor: a reference that does not resolve at all (the empty
      // authority of "///x") is one more rejected request, not an exception for the caller to catch.
      if (!Uri.TryCreate(RemoteUri, remainingPath, out var upstream)) return null;

      // The scheme on its own, because an absolute reference can keep the configured host and path and change
      // only the scheme: that downgrades a credentialed https upstream to cleartext, and "file://<same
      // host>/<same path>" has the very same host+path form the containment check below compares.
      if (!string.Equals(upstream.Scheme, RemoteUri.Scheme, StringComparison.Ordinal)) return null;

      // Contained in the base, compared in the one form both the cache key and the UpstreamAuth match are
      // derived from (see GetHostPortPath), so "contained" means "cannot reach another prefix's cache entry
      // and cannot leave this prefix's auth scope". The configured path always ends in '/', so the sibling
      // "/open2/" cannot pass as a child of "/open/"; Ordinal because the key is case-sensitive too.
      if (!upstream.GetHostPortPath().StartsWith(RemoteUri.GetHostPortPath(), StringComparison.Ordinal))
        return null;

      // A query, a fragment or userinfo is excluded from that form yet still travels - upstream, or back to
      // the client as a redirect Location - so two requests differing only there would share one entry.
      // Nothing legitimate produces them: the query never enters the path, and '?', '#' and '@'-with-'//'
      // cannot appear in a remainder that stays a path.
      return upstream.Query.Length == 0 && upstream.Fragment.Length == 0 && upstream.UserInfo.Length == 0 ?
        upstream : null;
    }

    public override string ToString() => $"{Prefix}={RemoteUri}";
  }

  public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;

  public override IReadOnlyList<Endpoint> Endpoints { get; }
}
