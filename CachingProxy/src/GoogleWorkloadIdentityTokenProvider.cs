using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

namespace JetBrains.CachingProxy;

/// <summary>The one Google call the proxy makes, behind an interface so tests need no AWS or Google.</summary>
public interface IGoogleFederatedTokenClient
{
  Task<(string Token, DateTimeOffset ExpiresAt)> ExchangeAsync(string audience, CancellationToken ct);
}

/// <summary>
/// Exchanges the proxy's AWS identity for a Google federated access token at Google STS, through a workload
/// identity pool provider of type AWS. The subject token is a SigV4-signed <c>sts:GetCallerIdentity</c> that
/// Google replays to AWS to learn the caller's role, so no Google key exists anywhere.
/// <para>Google's client libraries read AWS credentials only from EC2 metadata, not from the ECS task-role
/// endpoint or web identity, hence the hand-rolled request; the credentials come from the SDK's default chain,
/// like <see cref="S3RequestSigner"/>.</para>
/// </summary>
public sealed class GoogleFederatedTokenClient(
  IHttpClientFactory httpClientFactory,
  TimeProvider timeProvider,
  AwsCredentialSource? credentialSource = null) : IGoogleFederatedTokenClient
{
  // The global endpoint, signed for us-east-1, so no region needs configuring. Google accepts regional ones too.
  private const string StsHost = "sts.amazonaws.com";
  private const string StsRegion = "us-east-1";
  private const string GetCallerIdentityQuery = "Action=GetCallerIdentity&Version=2011-06-15";

  // Binds the signed request to one provider, so it cannot be replayed against another.
  private const string TargetResourceHeader = "x-goog-cloud-target-resource";

  private static readonly Uri ourTokenEndpoint = new("https://sts.googleapis.com/v1/token");

  private readonly AwsCredentialSource myCredentialSource = credentialSource ??
    (static ct => DefaultAWSCredentialsIdentityResolver.GetCredentials().GetCredentialsAsync());

  public async Task<(string Token, DateTimeOffset ExpiresAt)> ExchangeAsync(string audience, CancellationToken ct)
  {
    var credentials = await myCredentialSource(ct);
    var now = timeProvider.GetUtcNow();
    using var http = httpClientFactory.CreateClient(nameof(GoogleFederatedTokenClient));
    using var response = await http.PostAsJsonAsync(ourTokenEndpoint, new TokenExchangeRequest
    {
      Audience = audience,
      SubjectToken = CreateSubjectToken(credentials, audience, now),
    }, ct);
    if (!response.IsSuccessStatusCode)
    {
      // Google's error body names the cause (rejected attribute condition, bad signature) and holds no secret.
      var body = await response.Content.ReadAsStringAsync(ct);
      throw new InvalidOperationException(
        $"Google STS refused the token exchange for {audience}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }

    var result = await response.Content.ReadFromJsonAsync<TokenExchangeResponse>(ct);
    if (result?.AccessToken is not { Length: > 0 } token)
      throw new InvalidOperationException($"Google STS returned an empty access token for {audience}.");
    return (token, now + TimeSpan.FromSeconds(result.ExpiresIn));
  }

  /// <summary>
  /// The <c>aws4_request</c> subject token: a signed GetCallerIdentity, serialized as JSON and URL-encoded. Public
  /// for the test, which checks the request Google will replay.
  /// </summary>
  public static string CreateSubjectToken(ImmutableCredentials credentials, string audience, DateTimeOffset now)
  {
    using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{StsHost}/?{GetCallerIdentityQuery}");
    request.Headers.TryAddWithoutValidation(TargetResourceHeader, audience);
    S3RequestSigner.Sign(request, credentials, StsRegion, "sts", now, TargetResourceHeader);

    var headers = request.Headers
      .Select(static h => new SubjectTokenHeader(h.Key, string.Join(",", h.Value)))
      .Prepend(new SubjectTokenHeader("host", StsHost))
      .ToList();
    var json = JsonSerializer.Serialize(new SubjectToken($"https://{StsHost}?{GetCallerIdentityQuery}", "POST", headers));
    return Uri.EscapeDataString(json);
  }

  private sealed record SubjectToken(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("headers")] List<SubjectTokenHeader> Headers);

  private sealed record SubjectTokenHeader(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value);

  private sealed class TokenExchangeRequest
  {
    [JsonPropertyName("grant_type")] public string GrantType => "urn:ietf:params:oauth:grant-type:token-exchange";
    [JsonPropertyName("audience")] public required string Audience { get; init; }
    [JsonPropertyName("scope")] public string Scope => "https://www.googleapis.com/auth/cloud-platform";
    [JsonPropertyName("requested_token_type")] public string RequestedTokenType => "urn:ietf:params:oauth:token-type:access_token";
    [JsonPropertyName("subject_token_type")] public string SubjectTokenType => "urn:ietf:params:aws:token-type:aws4_request";
    [JsonPropertyName("subject_token")] public required string SubjectToken { get; init; }
  }

  private sealed class TokenExchangeResponse
  {
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; init; }
  }
}

/// <summary>
/// Caches Google federated access tokens per audience. Memory only, like <see cref="CodeArtifactTokenProvider"/>:
/// a bearer secret has no business in the shared L2. FusionCache also makes the exchange single-flight.
/// </summary>
public sealed class GoogleWorkloadIdentityTokenProvider(
  IGoogleFederatedTokenClient client,
  IFusionCache cache,
  TimeProvider timeProvider,
  ILogger<GoogleWorkloadIdentityTokenProvider> logger)
{
  // A federated token lasts 15 minutes, so refresh with 5 to spare.
  private static readonly TimeSpan ourRefreshSkew = TimeSpan.FromMinutes(5);

  // Lifetime of a token that comes back already inside the refresh window.
  private static readonly TimeSpan ourFallbackDuration = TimeSpan.FromMinutes(1);

  public ValueTask<string> GetTokenAsync(string audience, CancellationToken ct) =>
    cache.GetOrSetAsync<string>(
      $"google-wif-token::{audience}",
      async (ctx, innerCt) =>
      {
        var (token, expiresAt) = await client.ExchangeAsync(audience, innerCt);
        var ttl = expiresAt - timeProvider.GetUtcNow() - ourRefreshSkew;
        ctx.Options.SetDuration(ttl > TimeSpan.Zero ? ttl : ourFallbackDuration);
        logger.LogDebug("Obtained Google federated token for {Audience}, expires at {ExpiresAt}", audience, expiresAt);
        return token;
      },
      options: new FusionCacheEntryOptions().SetSkipDistributedCache(true, true),
      token: ct);
}
