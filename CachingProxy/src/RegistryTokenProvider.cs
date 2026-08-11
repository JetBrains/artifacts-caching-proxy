using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

namespace JetBrains.CachingProxy;

/// <summary>
/// One parsed <c>WWW-Authenticate: Bearer realm=…,service=…,scope=…</c> challenge from an OCI registry.
/// <see cref="Realm"/> and <see cref="Service"/> say where a token comes from; <see cref="Scope"/> says
/// what it is for, and only the registry knows how far it reaches. Remembered whole, per repository, so
/// later requests for the same image can mint a token without being challenged first.
/// <para><see cref="Scope"/> is empty for a token that asks for nothing in particular, which is what the
/// <c>/v2/</c> ping needs: both Docker Hub and Space challenge it and both then issue an unscoped token
/// that satisfies it.</para>
/// </summary>
public sealed record RegistryChallenge(Uri Realm, string? Service, string Scope);

/// <summary>
/// The OCI "registry token dance". Every mainstream registry (Docker Hub, ghcr.io, quay.io, the
/// JetBrains Space registries) answers a pull — <b>even an anonymous one</b> — with <c>401</c> plus a
/// <c>WWW-Authenticate: Bearer realm=…,service=…,scope=…</c> challenge, and expects the client to GET a
/// short-lived token from <c>realm</c> and retry. That is not an OAuth2 client-credentials grant, so it
/// does not fit either mode of <see cref="UpstreamAuth"/>; it is a per-repository token obtained from a
/// URL the registry names.
///
/// <para>Driven by the challenge rather than by configuration, so a public mirror needs no config at all
/// (which matters: the cache-redirector only emits an <c>UpstreamAuth</c> block for private prefixes, and
/// a prefix that has one is also gated behind an inbound client JWT — see <see cref="RemoteServers"/>).
/// Only prefixes whose caching profile sets <see cref="CachingProfile.Oci"/> take this path, so an
/// unrelated upstream cannot make us fetch a token from a URL it chose.</para>
///
/// <para>Tokens are cached in FusionCache keyed by (realm, service, scope) and, like the GitHub App
/// installation tokens, memory-only via
/// <see cref="FusionCacheEntryOptionsExtensions.SetSkipDistributedCache(FusionCacheEntryOptions,bool,bool)"/>:
/// these are short-lived secrets that must not be written to a shared (L2/Redis) store. FusionCache also
/// single-flights the mint, so a burst of concurrent pulls performs one token request.</para>
/// </summary>
public sealed class RegistryTokenProvider(
  IHttpClientFactory httpClientFactory,
  IFusionCache cache,
  ILogger<RegistryTokenProvider> logger)
{
  // Re-mint once the cached token is within this window of its expiry. Registry tokens are short-lived
  // (Docker Hub issues 300s), so the skew is correspondingly small.
  private static readonly TimeSpan ourRefreshSkew = TimeSpan.FromSeconds(30);

  // Floor for the cache lifetime, used when a registry returns a token that is already inside the
  // refresh window. Short enough to stay honest, long enough that one pull does not re-mint per request.
  private static readonly TimeSpan ourFallbackDuration = TimeSpan.FromSeconds(15);

  // Per the distribution spec, an omitted expires_in means 60 seconds.
  private static readonly TimeSpan ourDefaultDuration = TimeSpan.FromSeconds(60);

  // How long a repository's challenge is remembered so subsequent requests can mint a token proactively
  // instead of being challenged again. It is not a secret and it never changes in practice, so this is
  // only a bound on how long a genuine registry-side change goes unnoticed.
  private static readonly TimeSpan ourChallengeMemory = TimeSpan.FromHours(1);

  // The path segments the OCI distribution API defines after the repository name. Used to cut
  // "/v2/<name>/<verb>/<reference>" back to the part that identifies what is being pulled.
  private static readonly string[] ourApiVerbs = ["manifests", "blobs", "tags", "referrers"];

  // Registry tokens, and the realms they came from, stay in memory: the tokens are short-lived secrets
  // that must not reach a shared L2 store, and a realm is cheap enough to re-learn per replica. A fresh
  // object per call, never a shared static one, because GetOrSetAsync's factory mutates ctx.Options.
  private static FusionCacheEntryOptions MemoryOnly() =>
    new FusionCacheEntryOptions().SetSkipDistributedCache(true, true);

  /// <summary>
  /// The challenge to use without having been challenged: the one this repository issued last time, in
  /// full. Null until it has challenged us once.
  /// <para>Remembered per repository rather than per host because the scope is the registry's to define
  /// and cannot be computed from the path: the segments between the API root and the verb may be a
  /// repository name in full (Docker Hub answers <c>/v2/library/ubuntu/manifests/latest</c> with
  /// <c>repository:library/ubuntu:pull</c>) or a mirror prefix with an image inside it, where the scope
  /// names only the prefix (Space answers <c>/v2/p/ij/docker-hub/library/ubuntu/manifests/latest</c> with
  /// <c>repository:p/ij/docker-hub:pull</c>). Nothing in the URL says which, so we ask. Every request for
  /// one image shares the key, manifest and blobs alike, so a whole pull pays that single 401 once.</para>
  /// </summary>
  public async ValueTask<RegistryChallenge?> GetRememberedChallengeAsync(Uri upstreamUri, CancellationToken ct) =>
    await cache.GetOrDefaultAsync<RegistryChallenge>(ChallengeKey(upstreamUri), options: MemoryOnly(), token: ct);

  /// <summary>Remembers a repository's challenge so later requests for it skip the 401 round-trip.</summary>
  public ValueTask RememberChallengeAsync(Uri upstreamUri, RegistryChallenge challenge, CancellationToken ct) =>
    cache.SetAsync(ChallengeKey(upstreamUri), challenge, MemoryOnly().SetDuration(ourChallengeMemory), token: ct);

  /// <summary>
  /// The token for <paramref name="challenge"/>, minted on demand and cached until shortly before it
  /// expires. <paramref name="credentials"/> is the <c>Authorization</c> header the matched
  /// <see cref="UpstreamAuth"/> produced (see <see cref="IUpstreamAuthorizationProvider"/>) and is
  /// forwarded to the realm only when <see cref="MayForwardCredentials"/> allows it, so a service
  /// account is never handed to a token endpoint we did not vet. Returns null when the token request
  /// fails, leaving the caller with the registry's own 401 to relay.
  /// </summary>
  public async ValueTask<string?> GetTokenAsync(RegistryChallenge challenge, Uri upstreamUri, UpstreamAuth? auth,
    AuthenticationHeaderValue? credentials, CancellationToken ct)
  {
    if (!MayForwardCredentials(challenge.Realm, upstreamUri, auth))
      credentials = null;

    try
    {
      // The credentials are part of the identity of the cached token, not just of the request that
      // mints it: an anonymous token and a service-account token for one scope are different tokens.
      // Only the scheme and a hash of the parameter go into the key, never the credential itself.
      var credentialTag = credentials == null ? "anonymous" : $"{credentials.Scheme}:{credentials.Parameter?.GetHashCode()}";
      return await cache.GetOrSetAsync<string>(
        $"registry-token::{challenge.Realm}::{challenge.Service}::{challenge.Scope}::{credentialTag}",
        async (ctx, innerCt) =>
        {
          var (token, expiresIn) = await FetchTokenAsync(challenge, credentials, innerCt);
          var ttl = expiresIn - ourRefreshSkew;
          ctx.Options.SetDuration(ttl > TimeSpan.Zero ? ttl : ourFallbackDuration);
          return token;
        },
        options: MemoryOnly(),
        token: ct);
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
      logger.LogWarning(e, "Could not obtain a registry token from {Realm} for {Scope}: {Message}",
        challenge.Realm, challenge.Scope, e.Message);
      return null;
    }
  }

  /// <summary>
  /// Parses the <c>Bearer</c> challenge off a registry's 401. The scope is taken from the challenge when
  /// present, since some registries challenge without one; failing that it is derived from the request
  /// path, and failing that the token is asked for unscoped — which is exactly right for the <c>/v2/</c>
  /// ping, and for anything else costs one token request to arrive at the same 401.
  /// Returns null when there is no usable Bearer challenge, or when its realm is not an absolute HTTPS
  /// URL (loopback excepted, for tests): a token request carries credentials and must not go out in the
  /// clear or to a relative/opaque target.
  /// </summary>
  public static RegistryChallenge? TryParseChallenge(HttpResponseMessage response, Uri upstreamUri)
  {
    if (!response.Headers.NonValidated.TryGetValues("WWW-Authenticate", out var rawValues))
      return null;

    foreach (var raw in rawValues)
    {
      if (TryParseBearerParams(raw) is not { } parameters ||
          !parameters.TryGetValue("realm", out var realm) ||
          !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri) ||
          !realmUri.IsSecureOrLoopback())
        continue;

      var scope = parameters.GetValueOrDefault("scope") ?? TryDeriveScope(upstreamUri) ?? "";
      return new RegistryChallenge(realmUri, parameters.GetValueOrDefault("service"), scope);
    }

    return null;
  }

  /// <summary>
  /// The pull scope to fall back on when a challenge states none: <c>repository:&lt;name&gt;:pull</c>, with
  /// the name taken to be everything between the API root and the verb, after the shape the distribution
  /// API fixes — <c>/v2/&lt;name&gt;/&lt;verb&gt;/&lt;reference&gt;</c>, <c>/v2</c> sitting directly under
  /// the registry host because a client has no way to address it anywhere else. Null for anything not of
  /// that shape, including <c>/v2/_catalog</c> (whose scope is <c>registry:catalog:*</c>, and which the
  /// docker profile redirects rather than proxies).
  /// <para>A guess, and used only where there is nothing better: where a mirror is served under a prefix
  /// the registry may draw the repository boundary inside that path — Space answers
  /// <c>/v2/p/ij/docker-hub/library/ubuntu/manifests/latest</c> with <c>repository:p/ij/docker-hub:pull</c>
  /// — and the URL does not say where. A stated scope always wins, and the preemptive path uses the
  /// remembered challenge instead of this (see <see cref="GetRememberedChallengeAsync"/>).</para>
  /// </summary>
  public static string? TryDeriveScope(Uri upstreamUri)
  {
    var segments = upstreamUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.FirstOrDefault() != "v2") return null;

    var verb = Array.FindLastIndex(segments, segment => ourApiVerbs.Contains(segment));
    if (verb <= 1) return null;

    return $"repository:{string.Join('/', segments, 1, verb - 1)}:pull";
  }

  /// <summary>
  /// Whether the matched upstream's credentials may be sent to this realm. A realm on the upstream's own
  /// host and port is allowed outright (a registry that authenticates its own pulls), and anything else
  /// must be declared in <see cref="UpstreamAuth.TokenRealms"/>. So Docker Hub's cross-host
  /// <c>auth.docker.io</c> is still used — just anonymously, which is all a public mirror needs — while a
  /// compromised or spoofed upstream cannot redirect a service account to a realm of its choosing.
  /// <para>The port is part of "its own": another port is another service, which on a shared host may
  /// well belong to someone else. A registry that really does host its token endpoint elsewhere needs one
  /// <see cref="UpstreamAuth.TokenRealms"/> entry, and until it has one its pulls degrade to anonymous
  /// rather than leaking the account.</para>
  /// </summary>
  public static bool MayForwardCredentials(Uri realm, Uri upstreamUri, UpstreamAuth? auth) =>
    (string.Equals(realm.Host, upstreamUri.Host, StringComparison.OrdinalIgnoreCase) && realm.Port == upstreamUri.Port) ||
    (auth?.TokenRealms ?? []).Any(allowed => realm.AbsoluteUri.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));

  private async Task<(string Token, TimeSpan ExpiresIn)> FetchTokenAsync(
    RegistryChallenge challenge, AuthenticationHeaderValue? credentials, CancellationToken ct)
  {
    var builder = new UriBuilder(challenge.Realm);
    var query = builder.Query.TrimStart('?');
    // An empty scope is omitted rather than sent empty: the ping wants a token for nothing in particular,
    // and "scope=" is not the same request.
    var parameters = new List<string>();
    if (!string.IsNullOrEmpty(challenge.Scope))
      parameters.Add($"scope={Uri.EscapeDataString(challenge.Scope)}");
    if (!string.IsNullOrEmpty(challenge.Service))
      parameters.Add($"service={Uri.EscapeDataString(challenge.Service)}");
    builder.Query = string.Join('&', query.Length > 0 ? parameters.Prepend(query) : parameters);

    using var http = httpClientFactory.CreateClient();
    using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri) { Headers = { Authorization = credentials } };

    using var response = await http.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
      throw new InvalidOperationException(
        $"Registry token endpoint {challenge.Realm} answered {(int)response.StatusCode} {response.ReasonPhrase} for scope '{challenge.Scope}'.");

    var result = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
    // Docker Hub returns "token"; the OAuth2-shaped registries return "access_token". The spec allows
    // either and calls them interchangeable.
    if ((result?.Token ?? result?.AccessToken) is not { Length: > 0 } token)
      throw new InvalidOperationException($"Registry token endpoint {challenge.Realm} returned no token for scope '{challenge.Scope}'.");

    var expiresIn = result!.ExpiresIn > 0 ? TimeSpan.FromSeconds(result.ExpiresIn) : ourDefaultDuration;
    logger.LogDebug("Obtained a registry token from {Realm} for {Scope}, valid {ExpiresIn}",
      challenge.Realm, challenge.Scope, expiresIn);
    return (token, expiresIn);
  }

  // What the remembered challenge belongs to: the authority plus the path down to the API verb, so a
  // manifest and every blob of one image agree on it. A path with no verb - the /v2/ ping above all - is
  // its own key rather than no key, because it is challenged too and its (unscoped) token is just as
  // worth remembering.
  private static string ChallengeKey(Uri upstreamUri)
  {
    var segments = upstreamUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var verb = Array.FindLastIndex(segments, segment => ourApiVerbs.Contains(segment));
    var repository = string.Join('/', segments, 0, verb > 0 ? verb : segments.Length);

    return $"registry-challenge::{upstreamUri.Authority}::{repository}";
  }

  /// <summary>
  /// The auth-params of a <c>Bearer</c> challenge, e.g.
  /// <c>realm="https://auth.docker.io/token",service="registry.docker.io",scope="…"</c>, or null when
  /// <paramref name="raw"/> is not a Bearer challenge.
  /// <para>Read off the raw header (<c>Headers.NonValidated</c>) and split by hand rather than taken from
  /// <c>Headers.WwwAuthenticate</c>, because <c>WWW-Authenticate</c> is a comma-separated list of
  /// challenges whose auth-params are <i>also</i> comma-separated: the framework parser splits
  /// <c>Bearer realm="…",service="…"</c> at that comma and hands back <c>service="…"</c> as a separate
  /// challenge with its own scheme, so everything past the first param is lost. Commas inside a quoted
  /// string are literal, which is exactly the distinction that split misses.</para>
  /// </summary>
  private static Dictionary<string, string>? TryParseBearerParams(string raw)
  {
    var trimmed = raw.AsSpan().Trim();
    var space = trimmed.IndexOf(' ');
    if (space < 0 || !trimmed[..space].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
      return null;

    var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var rest = trimmed[(space + 1)..];
    var quoted = false;
    var start = 0;
    for (var i = 0; i <= rest.Length; i++)
    {
      if (i < rest.Length)
      {
        if (rest[i] == '"') quoted = !quoted;
        if (rest[i] != ',' || quoted) continue;
      }

      AddParameter(parameters, rest[start..i]);
      start = i + 1;
    }

    return parameters;
  }

  // One "name=value" auth-param; the value may be quoted or bare. Silently skips a malformed part rather
  // than failing the whole challenge, since a missing realm is already handled by the caller.
  private static void AddParameter(Dictionary<string, string> into, ReadOnlySpan<char> part)
  {
    var separator = part.IndexOf('=');
    if (separator < 0) return;

    var name = part[..separator].Trim();
    var value = part[(separator + 1)..].Trim().Trim('"');
    if (name.Length > 0 && value.Length > 0) into[name.ToString()] = value.ToString();
  }

  private sealed class TokenResponse
  {
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
  }
}
