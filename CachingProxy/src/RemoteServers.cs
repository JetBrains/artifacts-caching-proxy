using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy;

public partial class RemoteServers : EndpointDataSource
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
      // The credential is a property of the upstream, not of the inbound gate: a repository that is
      // individually public can still sit inside a private origin we may only fetch with our service
      // account. So match regardless of IsPrivate - that flag only decides gating below.
      var matched = MatchAuth(remoteUri, config.UpstreamAuth);
      var matchedAuth = matched?.Value;
      if (matchedAuth != null && !remoteUri.IsSecureOrLoopback())
        throw new ArgumentException(
          $"Authenticated upstream '{remoteUri}' must use HTTPS except on loopback.");
      var profile = ResolveProfile(prefix.Profile, config.CachingProfiles);
      ValidateDefaultNamespace(prefix, profile);
      var remoteServer = new RemoteServer(trimmedPrefix, remoteUri,
        config.CacheDuration.Union(prefix.CacheDuration), matchedAuth, profile)
      {
        // Non-null exactly when the profile resolved, so a blank name cannot reach the metric as an empty
        // label - which Prometheus reads as no label at all, forking a series off the "none" bucket.
        ProfileName = profile == null ? null : prefix.Profile,
        DefaultNamespace = prefix.DefaultNamespace
      };

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
      var isGated = prefix.IsPrivate ?? remoteServer.Auth != null;
      var metadata = isGated ?
        new EndpointMetadataCollection(remoteServer, new AuthorizeAttribute()) :
        new EndpointMetadataCollection(remoteServer);
      hasGatedOciPrefix |= isGated && remoteServer.Profile?.Oci == true;

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

  // A DefaultNamespace only means anything where there is a repository name to expand, which is the OCI
  // request shape and nothing else: set on any other prefix it would silently do nothing, so say so at
  // startup rather than leave someone to wonder why their pulls still 404. The value is spliced into an
  // upstream path, so hold it to the one path component the distribution spec allows - that also keeps
  // '/', '..' and an escape out of it by construction.
  private static void ValidateDefaultNamespace(CachingProxyPrefix prefix, CachingProfile? profile)
  {
    if (prefix.DefaultNamespace == null) return;
    if (profile is not { Oci: true })
      throw new ArgumentException(
        $"Prefix '{prefix}' sets DefaultNamespace, which only applies to a caching profile with Oci=true.");
    if (!OurOciPathComponent.IsMatch(prefix.DefaultNamespace))
      throw new ArgumentException(
        $"Prefix '{prefix}' has a DefaultNamespace that is not a single OCI path component.");
  }

  // The path-component grammar of the distribution spec's repository name.
  [GeneratedRegex("^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*$", RegexOptions.Compiled)]
  private static partial Regex OurOciPathComponent { get; }

  // An entry with no UrlPrefixes can never match an upstream, so every prefix it was meant to gate would be
  // left un-gated and fetched anonymously - and whatever those prefixes already hold in the cache would be
  // served to anyone. That is a half-configured secret failing open, so refuse to start, naming the entry so
  // the missing setting (UpstreamAuth__<name>__UrlPrefixes__0) is obvious. A blank prefix is the same hole:
  // it has no path to cover, so CoversPath rejects it for every upstream.
  private static void ValidateUpstreamAuth(Dictionary<string, UpstreamAuth> auths)
  {
    foreach (var (name, auth) in auths)
    {
      if (auth.UrlPrefixes.Length == 0)
        throw new ArgumentException(
          $"UpstreamAuth '{name}' has no UrlPrefixes, so it would gate nothing and leave its upstreams anonymous.");
      if (auth.UrlPrefixes.Any(string.IsNullOrWhiteSpace))
        throw new ArgumentException(
          $"UpstreamAuth '{name}' has a blank UrlPrefix, which covers no upstream and gates nothing.");
    }
  }

  // Among the auth entries whose UrlPrefixes cover the upstream URL, the longest (most specific) one wins,
  // so a host-wide block and a path-scoped block can coexist. Returns null when nothing matches, leaving the
  // upstream unauthenticated. The entry keeps its configuration name, which is what the credential-less
  // warning has to point at (UpstreamAuth__<name>__…) to be actionable.
  private static KeyValuePair<string, UpstreamAuth>? MatchAuth(
    Uri remoteUri, IReadOnlyDictionary<string, UpstreamAuth> auths)
  {
    var remotePrefix = remoteUri.GetHostPortPath();
    return auths.
      SelectMany(entry => entry.Value.UrlPrefixes.Select(prefix => (UrlPrefix: prefix, Entry: entry)))
      .Where(match => CoversPath(remotePrefix, match.UrlPrefix))
      .OrderByDescending(match => match.UrlPrefix.Length)
      .Select(match => (KeyValuePair<string, UpstreamAuth>?)match.Entry)
      .FirstOrDefault();
  }

  // A UrlPrefix is a path, not a character run, so it covers an upstream only down to a segment boundary. A
  // bare StartsWith once let the private `…/ij/jcp-github` claim the public `…/ij/jcp-github-mirror-public`:
  // that public prefix got an AuthorizeAttribute while the redirector, reading the same origin as public,
  // redirected clients to it unauthenticated, so every request 401'd (MRI-4837). A prefix already ending in
  // `/` carries its own boundary; otherwise the upstream has to end there or continue with one.
  private static bool CoversPath(string upstream, string urlPrefix) =>
    upstream.StartsWith(urlPrefix, StringComparison.OrdinalIgnoreCase) &&
    (urlPrefix.EndsWith('/') || upstream.Length == urlPrefix.Length || upstream[urlPrefix.Length] == '/');

  /// <summary>The prefix this request matched, or null when it matched none.</summary>
  public static RemoteServer? GetRemoteServer(HttpContext context) =>
    context.GetEndpoint()?.Metadata.GetMetadata<RemoteServer>();

  public static RemoteServer? GetRemoteServer(HttpContext context, out string? path)
  {
    path = context.GetRouteValue(PathParameterName)?.ToString();
    return GetRemoteServer(context);
  }

  public record RemoteServer(PathString Prefix, Uri RemoteUri, CacheDuration CacheDuration, UpstreamAuth? Auth = null, CachingProfile? Profile = null)
  {
    /// <summary>
    /// The <see cref="CachingProxyConfig.CachingProfiles"/> key naming <see cref="Profile"/>, or null for a
    /// prefix that declares none. Carried separately because <see cref="CachingProfile"/> has no identity of
    /// its own and <c>ResolveProfile</c> keeps only the compiled rules - and the name is the only handle a
    /// metric can slice a request by (see <see cref="CachingProxyMetrics.IncrementRequests"/>).
    /// </summary>
    public string? ProfileName { get; init; }

    /// <summary>
    /// The namespace an unqualified OCI repository name expands into (Docker Hub's <c>library</c>), or null
    /// for a prefix that declares none. See <see cref="WithDefaultNamespace"/>.
    /// </summary>
    public string? DefaultNamespace { get; init; }

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
    /// <para>The one rewrite applied to a URI that passed all of that is
    /// <see cref="WithDefaultNamespace"/>, which stays inside the base by construction.</para>
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

      // A query, a fragment or userinfo is excluded from that form yet would still travel upstream, so two
      // requests differing only there would share one entry. Nothing legitimate produces them: the routed
      // path never carries the request's own query - a Redirect rule appends that to its Location itself,
      // see RemoteProxy.ValidateRequestAsync - and '?', '#' and '@'-with-'//' cannot appear in a remainder
      // that stays a path.
      if (upstream.Query.Length > 0 || upstream.Fragment.Length > 0 || upstream.UserInfo.Length > 0)
        return null;

      return WithDefaultNamespace(upstream);
    }

    /// <summary>
    /// <paramref name="upstream"/> with <see cref="DefaultNamespace"/> spliced in front of a single-segment
    /// OCI repository name, or unchanged when there is no such name to expand.
    /// <para>Docker Hub keeps its unqualified images in an implicit namespace - <c>alpine</c> is really
    /// <c>library/alpine</c> - and a registry client expands that only when the registry domain is
    /// <c>docker.io</c> itself. Against a mirror the name travels as typed, so the pull addresses a
    /// repository that does not exist and Hub answers <c>401</c> (it reports an unknown repository as
    /// unauthorized, never 404). A mirror has to expand the name itself, and this is where: the cache key is
    /// this URI (see <c>CachingProxy.InvokeAsync</c>), so both spellings land on one entry instead of caching
    /// the same bytes twice, and the rewrite also covers the PrivateLink path that reaches us without
    /// passing the redirector.</para>
    /// <para>Only a name of exactly one segment, measured from <see cref="RemoteUri"/>'s own path so the rule
    /// still means "unqualified" for a mirror served under a project path. A name that is already namespaced
    /// is the caller's to spell, and a path that names no repository at all - the <c>/v2/</c> ping,
    /// <c>/v2/_catalog</c> - has nothing to expand.</para>
    /// </summary>
    private Uri WithDefaultNamespace(Uri upstream)
    {
      // The profile too, not just the setting: startup rejects the combination, but this record is
      // constructible on its own and the method rewrites an upstream path.
      if (DefaultNamespace == null || Profile is not { Oci: true }) return upstream;

      var verb = RegistryTokenProvider.SplitPath(upstream).Verb;
      var root = RegistryTokenProvider.SplitPath(RemoteUri).Segments.Length;
      if (verb - root != 1) return upstream;

      // Spliced into the path as text rather than rebuilt from the segments, so a digest reference and an
      // escape ('%2F' in a scoped name) reach the upstream exactly as the client wrote them. The base path
      // is kept verbatim and one literal segment goes in after it, so containment as checked above still
      // holds by construction - and the StartsWith says so rather than assuming the two APIs agree on
      // escaping.
      var basePath = RemoteUri.AbsolutePath;
      var path = upstream.AbsolutePath;
      if (!path.StartsWith(basePath, StringComparison.Ordinal)) return upstream;

      return new Uri(
        $"{upstream.GetLeftPart(UriPartial.Authority)}{basePath}{DefaultNamespace}/{path[basePath.Length..]}",
        UriKind.Absolute);
    }

    public override string ToString() => $"{Prefix}={RemoteUri}";
  }

  public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;

  public override IReadOnlyList<Endpoint> Endpoints { get; }
}
