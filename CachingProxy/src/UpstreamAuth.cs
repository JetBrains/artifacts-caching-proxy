using System;

namespace JetBrains.CachingProxy;

/// <summary>
/// Per-upstream authentication for one or more sources, configured separately from
/// <see cref="CachingProxyPrefix"/> and matched to an upstream by the longest <see cref="UrlPrefix"/>
/// of its resolved URL (see <see cref="RemoteServers"/>). This lets many prefixes pointing at the same
/// host share a single block.
///
/// An entry operates in one of two modes:
///
/// <para><b>Credential mode</b> (<see cref="ClientId"/> set): the proxy fetches an access token from
/// <see cref="TokenEndpoint"/> (the token request itself authenticates the client with HTTP Basic, the
/// library default) and sends it to the upstream as
/// <c>Authorization: Basic base64(<see cref="ClientId"/>:access_token)</c> — the client id is the Basic
/// username and the token is the password. The matched prefix serves proxy-fetched private artifacts, so
/// it also requires a validated inbound client JWT (see <see cref="RemoteServers"/>).</para>
///
/// <para><b>GitHub App mode</b> (<see cref="ClientId"/> set together with <see cref="PrivateKey"/>):
/// GitHub does not support the OAuth2 client-credentials grant, so a GitHub App authenticates
/// server-to-server by signing a short-lived RS256 JWT with its private key and exchanging it for an
/// installation access token (see <see cref="GitHubAppInstallationTokenProvider"/>).
/// The token is sent upstream as <c>Authorization: Bearer &lt;token&gt;</c> (Bearer by
/// default). Here <see cref="ClientId"/> is the JWT issuer (the App's client id); <see cref="TokenEndpoint"/>
/// and <see cref="ClientSecret"/> are not used. Like client-credentials mode it serves proxy-fetched
/// private artifacts, so it also requires a validated inbound client JWT.</para>
/// </summary>
public record UpstreamAuth
{
  // Upstream URL prefix this auth applies to (e.g. "https://repo.example.com/"); the longest match wins.
  public required string UrlPrefix { get; init; }

  public Uri? TokenEndpoint { get; init; }
  public string? ClientId { get; init; }
  public string? ClientSecret { get; init; }

  // Optional space-separated OAuth scopes, added to the token request when set.
  public string? Scope { get; init; }

  // GitHub App mode (used instead of TokenEndpoint/ClientSecret). Supply the App's RSA private key inline
  // as PEM text (PrivateKey); ClientId is reused as the JWT issuer. InstallationId is optional — when
  // omitted it is auto-resolved if the App has exactly one installation. GitHubApiBaseUrl defaults to GitHub.com.
  public string? PrivateKey { get; init; }
  public long? InstallationId { get; init; }
  public string GitHubApiBaseUrl { get; init; } = "https://api.github.com";

  // True when this entry uses GitHub App auth (a private key is configured); ClientId then acts as the
  // JWT issuer rather than an OAuth client id.
  public bool IsGitHubApp => !string.IsNullOrEmpty(PrivateKey);
}
