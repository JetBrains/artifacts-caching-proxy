using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Duende.AccessTokenManagement;
using Duende.IdentityModel.Client;

namespace JetBrains.CachingProxy;

public static class UpstreamAuthExtensions
{
  /// <summary>
  /// Builds the <c>Authorization</c> header sent to an authenticated upstream. The OAuth
  /// client-credentials access token is obtained (and cached/refreshed) by the token manager; we only
  /// turn it into a Basic header, since these upstreams expect the token as the Basic <em>password</em>
  /// (with the client id as the username) rather than a Bearer token. Returns <c>null</c> when
  /// <paramref name="auth"/> is <c>null</c> (unauthenticated upstream — no header is added).
  /// </summary>
  public static async Task<AuthenticationHeaderValue?> GetUpstreamAuthorizationHeaderAsync(
    this IClientCredentialsTokenManager tokenManager, UpstreamAuth auth, CancellationToken ct)
  {
    var token = await tokenManager
      .GetAccessTokenAsync(ClientCredentialsClientName.Parse(auth.ClientId), ct: ct)
      .GetToken();

    return new BasicAuthenticationHeaderValue(auth.ClientId, token.AccessToken);
  }
}
