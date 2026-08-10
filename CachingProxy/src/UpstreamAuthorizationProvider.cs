using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Duende.AccessTokenManagement;
using Duende.IdentityModel.Client;

namespace JetBrains.CachingProxy;

/// <summary>
/// Builds the <c>Authorization</c> header sent to an authenticated upstream, dispatching on the matched
/// <see cref="UpstreamAuth"/> entry's mode. Returns <c>null</c> when there is nothing to add (unauthenticated
/// upstream, or an entry with neither a client id nor a service account).
/// <para>For an OCI upstream the result goes somewhere else entirely: a registry takes an account at the
/// token endpoint its challenge names, not on <c>/v2/…</c>, so <see cref="RegistryTokenProvider"/>
/// presents this header there and only the minted token is sent to the registry. The account itself never
/// reaches <c>/v2/…</c> — see <see cref="RemoteProxy"/>.</para>
/// </summary>
public interface IUpstreamAuthorizationProvider
{
  ValueTask<AuthenticationHeaderValue?> GetAuthorizationHeaderAsync(UpstreamAuth? auth, CancellationToken ct);
}

public sealed class UpstreamAuthorizationProvider(
  GitHubAppInstallationTokenProvider gitHubAppTokenProvider,
  IClientCredentialsTokenManager? tokenManager = null) : IUpstreamAuthorizationProvider
{
  public async ValueTask<AuthenticationHeaderValue?> GetAuthorizationHeaderAsync(UpstreamAuth? auth, CancellationToken ct)
  {
    if (auth == null)
      return null;

    // Service-account mode: a fixed credential with nothing to exchange, so it is simply encoded. Checked
    // first because these entries carry no ClientId at all.
    if (auth.IsServiceAccount)
      return new BasicAuthenticationHeaderValue(auth.Username!, auth.Password ?? "");

    if (string.IsNullOrEmpty(auth.ClientId))
      return null;

    // GitHub App mode: exchange a signed JWT for an installation token and send it as Bearer (the App's
    // installation token, not an OAuth access token). See GitHubAppInstallationTokenProvider.
    if (auth.IsGitHubApp)
    {
      var installationToken = await gitHubAppTokenProvider.GetInstallationTokenAsync(auth, ct);
      return new AuthenticationHeaderValue("Bearer", installationToken);
    }

    // Client-credentials mode: the OAuth access token is obtained (and cached/refreshed) by the token
    // manager; we only turn it into a Basic header, since these upstreams expect the token as the Basic
    // password (with the client id as the username) rather than a Bearer token.
    if (tokenManager == null)
      return null;

    var token = await tokenManager
      .GetAccessTokenAsync(ClientCredentialsClientName.Parse(auth.ClientId), ct: ct)
      .GetToken();

    return new BasicAuthenticationHeaderValue(auth.ClientId, token.AccessToken);
  }
}
