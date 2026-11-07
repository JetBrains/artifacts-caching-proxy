using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    var publicUrl = ParsePublicUrl(config.PublicUrl);

    // Parsed up front, so a prefix's rewrite can map the other prefixes' roots too.
    var parsed = config.Prefixes.Select(prefix => ParsePrefix(prefix, config.UpstreamAuth)).ToArray();
    var authScopes = config.UpstreamAuth.Values
      .SelectMany(static auth => auth.UrlPrefixes.Select(urlPrefix => (urlPrefix, auth))).ToArray();

    var endpoints = new Endpoint[config.Prefixes.Length];
    var hasGatedOciPrefix = false;
    logger.LogInformation("Creating {Count} endpoints", config.Prefixes.Length);
    for (var i = 0; i < config.Prefixes.Length; i++)
    {
      var prefix = config.Prefixes[i];
      var (trimmedPrefix, remoteUri, matched, isGated, template) = parsed[i];
      var matchedAuth = matched?.Value;
      if (matchedAuth != null && !remoteUri.IsSecureOrLoopback())
        throw new ArgumentException(
          $"Authenticated upstream '{remoteUri}' must use HTTPS except on loopback.");
      var profile = ResolveProfile(prefix.Profile, config.CachingProfiles);
      ValidateDefaultNamespace(prefix, profile);
      // Both are built from one fixed root: the namespace splice measures it, and a rewrite holds one per
      // prefix, which per owner and repository would be a cache keyed by whatever the caller sends.
      if (template != null && (prefix.DefaultNamespace != null || profile?.Rules.Any(static rule => rule.RewriteUrls) == true))
        throw new ArgumentException(
          $"Templated prefix '{prefix}' cannot set DefaultNamespace or use a profile that rewrites URLs.");
      // A prefix is the redirector alias it serves, so the redirector serves this upstream root at the public
      // URL plus the prefix path. Only where the profile rewrites anything.
      var publicUri = publicUrl != null && profile?.Rules.Any(static rule => rule.RewriteUrls) == true
        ? PublicRootOf(publicUrl, trimmedPrefix)
        : null;
      var remoteServer = new RemoteServer(trimmedPrefix, remoteUri,
        config.CacheDuration.Union(prefix.CacheDuration), matchedAuth, profile)
      {
        // Non-null exactly when the profile resolved, so a blank name cannot reach the metric as an empty
        // label - which Prometheus reads as no label at all, forking a series off the "none" bucket.
        ProfileName = profile == null ? null : prefix.Profile,
        DefaultNamespace = prefix.DefaultNamespace,
        Retention = ParseRetention(prefix),
        UrlRewrite = publicUri == null ? null
          : new UpstreamUrlRewrite(remoteUri, publicUri, OtherRoots(config.Prefixes, parsed, i, publicUrl!)),
        UpstreamTemplate = template,
        AuthScopes = template == null ? [] : authScopes,
      };

      logger.LogInformation("RemoteServer: {Prefix} -> {RemoteUri}, Auth: {Auth}, Profile: {Profile}, PublicUrl: {PublicUrl}, Retention: {Retention}",
        trimmedPrefix, remoteServer.Upstream, remoteServer.Auth, prefix.Profile, publicUri, remoteServer.Retention);
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
      var metadata = isGated ?
        new EndpointMetadataCollection(remoteServer, new AuthorizeAttribute()) :
        new EndpointMetadataCollection(remoteServer);
      hasGatedOciPrefix |= isGated && remoteServer.Profile?.Oci == true;

      // Overlapping prefixes (e.g. "/aprefix" and "/aprefix/too") both match via their {**path}
      // catch-all, and routing breaks such ties by Endpoint.Order, NOT by specificity. So order by
      // descending prefix length here, making the longer (more specific) prefix win regardless of the
      // order it was declared in. Equal-length prefixes can never match the same path, so ties between
      // them are never observed - except two templates of one shape ("/gh/{a}/x" and "/gh/{b}/x"), which
      // routing answers with an AmbiguousMatchException. Whoever generates the prefixes must not emit those.
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

  // An unknown value has no lifecycle rule, so its objects would never expire.
  private static RetentionPolicy? ParseRetention(CachingProxyPrefix prefix)
  {
    try
    {
      return RetentionPolicy.Parse(prefix.Retention);
    }
    catch (ArgumentException e)
    {
      throw new ArgumentException($"Prefix '{prefix}': {e.Message}", e);
    }
  }

  private readonly record struct ParsedPrefix(
    string Path, Uri RemoteUri, KeyValuePair<string, UpstreamAuth>? MatchedAuth, bool IsGated,
    string[]? UpstreamTemplate);

  private static ParsedPrefix ParsePrefix(CachingProxyPrefix prefix, IReadOnlyDictionary<string, UpstreamAuth> auths)
  {
    var trimmed = prefix.Prefix.Trim('/');
    if (trimmed.Length == 0) throw new ArgumentException("Prefix is empty: " + prefix);

    var index = trimmed.IndexOf('=');
    var trimmedPrefix = index < 0 ? $"/{trimmed}" : $"/{trimmed[..index]}";

    var target = index < 0 ? trimmed : trimmed[(index + 1)..];
    target = target.TrimEnd('/') + '/';
    var (head, template) = SplitTemplate(prefix, trimmedPrefix, target);
    var remoteUri = Uri.TryCreate(head, UriKind.Absolute, out var targetUri) ? targetUri :
      new Uri(Uri.UriSchemeHttps + Uri.SchemeDelimiter + head, UriKind.Absolute);
    // A query or a fragment leaves the base with no trailing-slash path to resolve a request against
    // ("host/p?x=1" parses as path "/p" plus query), so every request to such a prefix would resolve
    // outside it and be rejected. Fail at startup rather than on every request. A template's literal
    // segments count too, so check them with a sample value in each placeholder.
    var sample = template == null ? remoteUri
      : new Uri(remoteUri, string.Concat(template.Select(static s => (IsPlaceholder(s) ? "x" : s) + '/')));
    if (sample.Query.Length > 0 || sample.Fragment.Length > 0)
      throw new ArgumentException($"Prefix target must have no query or fragment: {prefix}");
    // The credential is a property of the upstream, not of the inbound gate: a repository that is
    // individually public can still sit inside a private origin we may only fetch with our service
    // account. So match regardless of IsPrivate - that flag only decides gating.
    // A template matches on its literal head, so the credential it gets covers every value a caller may put
    // after it: whatever that credential can read upstream, this prefix serves. Keep the head as narrow as
    // the credential's intended reach.
    var matched = MatchAuth(remoteUri, auths);
    return new ParsedPrefix(trimmedPrefix, remoteUri, matched, prefix.IsPrivate ?? matched?.Value != null, template);
  }

  /// <summary>
  /// Splits a target into its literal head and, for a templated prefix (<c>/github.com/{owner}/{repo}</c>), the
  /// path segments from the first placeholder on; the template is null for a plain prefix.
  /// <para>A placeholder is a whole path segment <c>{name}</c> and the alias and the target name the same set,
  /// so every value is used exactly once in the upstream. Never in the host: a value the caller chooses must
  /// not pick where we connect. Only the plain form, since a default, a constraint or an optional parameter
  /// would let routing fill a value the upstream template cannot reproduce.</para>
  /// </summary>
  private static (string Head, string[]? Template) SplitTemplate(CachingProxyPrefix prefix, string alias, string target)
  {
    var aliasNames = AliasPlaceholders(prefix, alias);
    var brace = target.IndexOfAny(['{', '}']);
    if (brace < 0)
    {
      if (aliasNames.Count > 0)
        throw new ArgumentException($"Prefix '{prefix}' has placeholders in its alias but none in its target.");
      return (target, null);
    }

    var segmentStart = target.LastIndexOf('/', brace) + 1;
    var head = target[..segmentStart];
    // "https://{x}.host/" splits after "//", which leaves no host.
    if (segmentStart == 0 || head.EndsWith("//", StringComparison.Ordinal) ||
        !Uri.TryCreate(head.Contains(Uri.SchemeDelimiter) ? head : Uri.UriSchemeHttps + Uri.SchemeDelimiter + head,
          UriKind.Absolute, out _))
      throw new ArgumentException($"Prefix '{prefix}' has a placeholder in its target host.");

    var template = target[segmentStart..].TrimEnd('/').Split('/');
    var targetNames = new List<string>();
    foreach (var segment in template)
    {
      if (IsPlaceholder(segment) && OurPlaceholderName.IsMatch(segment[1..^1]))
        targetNames.Add(segment[1..^1]);
      else if (segment.IndexOfAny(['{', '}']) >= 0)
        throw new ArgumentException(
          $"Prefix '{prefix}' has a malformed target placeholder '{segment}'; use a whole segment {{name}}.");
    }

    if (targetNames.Count != targetNames.Distinct(StringComparer.Ordinal).Count() ||
        !targetNames.ToHashSet(StringComparer.Ordinal).SetEquals(aliasNames))
      throw new ArgumentException(
        $"Prefix '{prefix}' must use each of its alias placeholders exactly once in its target.");
    return (head, template);
  }

  private static HashSet<string> AliasPlaceholders(CachingProxyPrefix prefix, string alias)
  {
    var names = new HashSet<string>(StringComparer.Ordinal);
    if (alias.IndexOfAny(['{', '}']) < 0) return names;

    RoutePattern pattern;
    try
    {
      pattern = RoutePatternFactory.Parse(alias);
    }
    catch (RoutePatternException e)
    {
      throw new ArgumentException($"Prefix '{prefix}' has a malformed alias template: {e.Message}", e);
    }

    foreach (var segment in pattern.PathSegments.Where(static s => s.Parts.Any(static p => p.IsParameter)))
    {
      if (segment.Parts is not [RoutePatternParameterPart parameter] ||
          parameter.IsOptional || parameter.IsCatchAll || parameter.Default != null ||
          parameter.ParameterPolicies.Count > 0 || !OurPlaceholderName.IsMatch(parameter.Name) ||
          string.Equals(parameter.Name, PathParameterName, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException(
          $"Prefix '{prefix}' has an unsupported alias placeholder; use a whole segment {{name}}, name not '{PathParameterName}'.");
      names.Add(parameter.Name);
    }
    return names;
  }

  private static bool IsPlaceholder(string segment) =>
    segment.Length > 2 && segment[0] == '{' && segment[^1] == '}';

  [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
  private static partial Regex OurPlaceholderName { get; }

  // RFC 3986 unreserved, so a value needs no escaping and a cache key built from it is the URL as sent.
  // Without '%', a value cannot smuggle in the '%2e%2e' that System.Uri collapses, nor a '%2F'.
  [GeneratedRegex("^[A-Za-z0-9._~-]+$")]
  private static partial Regex OurPlaceholderValue { get; }

  private static Uri PublicRootOf(Uri publicUrl, string prefixPath) =>
    new(publicUrl.AbsoluteUri + prefixPath.TrimStart('/') + '/', UriKind.Absolute);

  /// <summary>
  /// The roots of the other prefixes a document under prefix <paramref name="self"/> may link to (MRI-5318): those
  /// with the same profile, as one ecosystem's documents link to its own hosts - a pypi.org simple index to
  /// files.pythonhosted.org. Never a gated one from an ungated prefix, whose anonymous client the gate would
  /// turn away. Of several prefixes on one root, the one named after it, else the first.
  /// </summary>
  private static IEnumerable<(Uri Upstream, Uri Public)> OtherRoots(
    CachingProxyPrefix[] prefixes, ParsedPrefix[] parsed, int self, Uri publicUrl) =>
    Enumerable.Range(0, prefixes.Length)
      .Where(j => j != self && parsed[j].UpstreamTemplate == null && prefixes[j].Profile == prefixes[self].Profile &&
                  (!parsed[j].IsGated || parsed[self].IsGated))
      .GroupBy(j => parsed[j].RemoteUri.AbsoluteUri, StringComparer.Ordinal)
      .Select(group =>
      {
        var chosen = group.FirstOrDefault(j => parsed[j].Path.TrimStart('/') + '/' == parsed[j].RemoteUri.GetHostPortPath(), group.First());
        return (parsed[chosen].RemoteUri, PublicRootOf(publicUrl, parsed[chosen].Path));
      });

  // Normalized to end in '/', so a prefix path appends to it.
  private static Uri? ParsePublicUrl(string? publicUrl)
  {
    if (string.IsNullOrWhiteSpace(publicUrl)) return null;
    if (!Uri.TryCreate(publicUrl.TrimEnd('/') + '/', UriKind.Absolute, out var uri) ||
        uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp ||
        uri.Query.Length > 0 || uri.Fragment.Length > 0)
      throw new ArgumentException($"PublicUrl '{publicUrl}' is not an absolute http(s) URL without query or fragment.");
    return uri;
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

    /// <summary>Null keeps the global cleanup and leaves S3 objects untagged.</summary>
    public RetentionPolicy? Retention { get; init; }

    /// <summary>The rewrite of this prefix's upstream root to its public URL, or null for a prefix without one.</summary>
    public UpstreamUrlRewrite? UrlRewrite { get; init; }

    /// <summary>
    /// For a templated prefix (<c>/github.com/{owner}/{repo}=…</c>), the upstream path segments from the first
    /// placeholder on, a placeholder kept as <c>{name}</c>; <see cref="RemoteUri"/> is then only the literal
    /// head before them. Null for a plain prefix.
    /// </summary>
    public IReadOnlyList<string>? UpstreamTemplate { get; init; }

    /// <summary>Every UpstreamAuth UrlPrefix with its entry, for a templated prefix to check against; empty otherwise.</summary>
    public IReadOnlyList<(string UrlPrefix, UpstreamAuth Auth)> AuthScopes { get; init; } = [];

    /// <summary>The configured upstream, the template included, for logs.</summary>
    public string Upstream => UpstreamTemplate == null ? RemoteUri.AbsoluteUri
      : RemoteUri.AbsoluteUri + string.Concat(UpstreamTemplate.Select(static segment => segment + '/'));

    /// <summary>
    /// <see cref="RemoteUri"/>, or for a templated prefix the base its route values fill in, or <c>null</c>
    /// when a value is not one path segment we can use as is.
    /// <para>Only unreserved characters, and never <c>.</c> or <c>..</c>: a value is spliced into the path
    /// unescaped, and a dot segment there would move the base itself, which no containment check measured
    /// against that base can see.</para>
    /// </summary>
    private Uri? GetBaseUri(RouteValueDictionary? routeValues)
    {
      if (UpstreamTemplate == null) return RemoteUri;

      var path = new StringBuilder(RemoteUri.AbsoluteUri);
      foreach (var segment in UpstreamTemplate)
      {
        if (IsPlaceholder(segment))
        {
          if (routeValues?[segment[1..^1]] is not string value || !OurPlaceholderValue.IsMatch(value) ||
              value is "." or "..")
            return null;
          path.Append(value);
        }
        else
          path.Append(segment);
        path.Append('/');
      }
      return new Uri(path.ToString(), UriKind.Absolute);
    }

    /// <summary>
    /// Whether <paramref name="upstream"/> falls to another credential than <see cref="Auth"/>, by the longest
    /// covering UpstreamAuth UrlPrefix as at startup. A template reaches upstream paths no one configured, and
    /// one in another entry's scope is that entry's to fetch and gate: an ungated template would serve its
    /// cache entries to anyone (MRI-4842), a credentialed one would fetch them with the wrong credential.
    /// Routing sends a path to a literal prefix over a template already, so this is for a scope no prefix
    /// spells out, an owner- or host-wide one.
    /// </summary>
    private bool InForeignAuthScope(Uri upstream)
    {
      // A plain prefix's upstream is fixed, and so is its credential: startup matched it already.
      if (UpstreamTemplate == null) return false;

      var path = upstream.GetHostPortPath();
      var owner = AuthScopes
        .Where(scope => CoversPath(path, scope.UrlPrefix))
        .OrderByDescending(static scope => scope.UrlPrefix.Length)
        .Select(static scope => scope.Auth)
        .FirstOrDefault();
      return !ReferenceEquals(owner, Auth);
    }

    /// <summary>
    /// The rewrite to apply to a body under <paramref name="rule"/>, or null for none. On the host of the
    /// redirector deployment a signed request came through, if it named one (MRI-4855).
    /// </summary>
    public UpstreamUrlRewrite? UrlRewriteFor(CachingRule? rule, HttpContext context) =>
      rule is not { RewriteUrls: true } || UrlRewrite is not { } rewrite ? null
      : context.Items[CachingProxyConstants.PublicHostItemKey] is string host ? rewrite.OnHost(host)
      : rewrite;

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
    /// <para>For a templated prefix the base is <see cref="RemoteUri"/> plus <see cref="UpstreamTemplate"/>
    /// filled from <paramref name="routeValues"/>, see <see cref="GetBaseUri"/>.</para>
    /// </summary>
    public Uri? GetUpstreamUri(string? remainingPath, RouteValueDictionary? routeValues = null)
    {
      if (GetBaseUri(routeValues) is not { } baseUri) return null;

      if (string.IsNullOrEmpty(remainingPath)) return InForeignAuthScope(baseUri) ? null : baseUri;

      // TryCreate, not the throwing constructor: a reference that does not resolve at all (the empty
      // authority of "///x") is one more rejected request, not an exception for the caller to catch.
      if (!Uri.TryCreate(baseUri, remainingPath, out var upstream)) return null;

      // The scheme on its own, because an absolute reference can keep the configured host and path and change
      // only the scheme: that downgrades a credentialed https upstream to cleartext, and "file://<same
      // host>/<same path>" has the very same host+path form the containment check below compares.
      if (!string.Equals(upstream.Scheme, baseUri.Scheme, StringComparison.Ordinal)) return null;

      // Contained in the base, compared in the one form both the cache key and the UpstreamAuth match are
      // derived from (see GetHostPortPath), so "contained" means "cannot reach another prefix's cache entry
      // and cannot leave this prefix's auth scope". The configured path always ends in '/', so the sibling
      // "/open2/" cannot pass as a child of "/open/"; Ordinal because the key is case-sensitive too.
      if (!upstream.GetHostPortPath().StartsWith(baseUri.GetHostPortPath(), StringComparison.Ordinal))
        return null;

      if (InForeignAuthScope(upstream)) return null;

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

    public override string ToString() => UpstreamTemplate == null ? $"{Prefix}={RemoteUri}" : $"{Prefix}={Upstream}";
  }

  public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;

  public override IReadOnlyList<Endpoint> Endpoints { get; }
}
