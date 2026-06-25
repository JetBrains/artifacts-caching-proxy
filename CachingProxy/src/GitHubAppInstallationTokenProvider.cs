using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ZiggyCreatures.Caching.Fusion;

namespace JetBrains.CachingProxy;

/// <summary>
/// Mints and caches GitHub App installation access tokens for server-to-server upstream auth. GitHub does
/// not support the OAuth2 client-credentials grant; instead a GitHub App authenticates by signing a
/// short-lived RS256 JWT with its private key (issuer = the App's client id) and exchanging it for an
/// installation token (valid ~1h) at <c>/app/installations/{id}/access_tokens</c>. The token is cached in
/// FusionCache with <see cref="FusionCacheEntryOptions.SetSkipDistributedCache"/> — memory only, since
/// these are short-lived secrets that must not be written to a shared (L2/Redis) store. FusionCache also
/// provides the single-flight (cache-stampede) protection, so concurrent requests don't all mint at once.
/// </summary>
public sealed class GitHubAppInstallationTokenProvider(
  IHttpClientFactory httpClientFactory,
  IFusionCache cache,
  TimeProvider timeProvider,
  ILogger<GitHubAppInstallationTokenProvider> logger)
{
  // Refresh once the cached token is within this window of its expiry (installation tokens last ~1h).
  private static readonly TimeSpan ourRefreshSkew = TimeSpan.FromMinutes(5);

  // Fallback lifetime if GitHub ever returns a token already inside the refresh window.
  private static readonly TimeSpan ourFallbackDuration = TimeSpan.FromMinutes(1);

  public ValueTask<string> GetInstallationTokenAsync(UpstreamAuth auth, CancellationToken ct) =>
    cache.GetOrSetAsync<string>(
      // GitHub App entries always set ClientId (the JWT issuer); use it as the per-app cache key.
      $"github-app-installation-token::{auth.ClientId}",
      async (ctx, innerCt) =>
      {
        var (token, expiresAt) = await FetchInstallationTokenAsync(auth, innerCt);
        // Size the cache lifetime so the entry expires (and is re-minted) shortly before GitHub does.
        var ttl = expiresAt - timeProvider.GetUtcNow() - ourRefreshSkew;
        ctx.Options.SetDuration(ttl > TimeSpan.Zero ? ttl : ourFallbackDuration);
        return token;
      },
      // Keep these short-lived secrets out of any distributed (L2) cache — memory only.
      options: new FusionCacheEntryOptions().SetSkipDistributedCache(true, true),
      token: ct);

  private async Task<(string Token, DateTimeOffset ExpiresAt)> FetchInstallationTokenAsync(UpstreamAuth auth, CancellationToken ct)
  {
    var jwt = CreateAppJwt(auth);
    using var http = httpClientFactory.CreateClient();
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    http.BaseAddress = new Uri(auth.GitHubApiBaseUrl);

    var installationId = auth.InstallationId ?? await ResolveInstallationIdAsync(http, jwt, auth, ct);

    using var request = new HttpRequestMessage(HttpMethod.Post, $"/app/installations/{installationId}/access_tokens");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

    using var response = await http.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync(ct);
      throw new InvalidOperationException(
        $"GitHub App '{auth.ClientId}' could not obtain an installation token for installation {installationId}: " +
        $"{(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }

    var result = await response.Content.ReadFromJsonAsync<InstallationTokenResponse>(ct);
    if (result?.Token is not { Length: > 0 })
      throw new InvalidOperationException($"GitHub App '{auth.ClientId}' returned an empty installation token.");

    logger.LogDebug("Obtained GitHub App installation token for installation {InstallationId}, expires at {ExpiresAt}",
      installationId, result.ExpiresAt);
    return (result.Token, result.ExpiresAt);
  }

  private async Task<long> ResolveInstallationIdAsync(HttpClient http, string jwt, UpstreamAuth auth, CancellationToken ct)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, $"/app/installations");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

    using var response = await http.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync(ct);
      throw new InvalidOperationException(
        $"GitHub App '{auth.ClientId}' could not list installations: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }

    var installations = await response.Content.ReadFromJsonAsync<List<Installation>>(ct) ?? [];
    if (installations.Count == 0)
      throw new InvalidOperationException(
        $"GitHub App '{auth.ClientId}' has no installations; install the App on the target account/org first.");
    if (installations.Count > 1)
      throw new InvalidOperationException(
        $"GitHub App '{auth.ClientId}' has {installations.Count} installations; set InstallationId explicitly in UpstreamAuth.");

    return installations[0].Id;
  }

  private string CreateAppJwt(UpstreamAuth auth)
  {
    using var rsa = RSA.Create();
    rsa.ImportFromPem(auth.PrivateKey); // handles both PKCS#1 ("RSA PRIVATE KEY") and PKCS#8 PEM

    var now = timeProvider.GetUtcNow();
    var key = new RsaSecurityKey(rsa)
    {
      // Don't cache the signature provider: it would hold a reference to the RSA we dispose at the end of
      // this method, breaking the next call that reuses the cached provider.
      CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
    };

    var descriptor = new SecurityTokenDescriptor
    {
      Issuer = auth.ClientId,
      IssuedAt = now.AddSeconds(-60).UtcDateTime, // backdate slightly to tolerate clock skew
      Expires = now.AddMinutes(9).UtcDateTime,    // GitHub rejects an expiry more than 10 minutes out
      SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
    };
    return new JsonWebTokenHandler().CreateToken(descriptor);
  }

  private sealed class InstallationTokenResponse
  {
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }
  }

  [SuppressMessage("ReSharper", "ClassNeverInstantiated.Local")]
  private sealed record Installation([property: JsonPropertyName("id")] long Id);
}
