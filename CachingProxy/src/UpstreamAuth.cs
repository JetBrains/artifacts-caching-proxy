using System;
using System.Text;

namespace JetBrains.CachingProxy;

/// <summary>
/// Per-upstream authentication for one or more sources, configured separately from
/// <see cref="CachingProxyPrefix"/> and matched to an upstream by the longest <see cref="UrlPrefixes"/>
/// of its resolved URL (see <see cref="RemoteServers"/>). This lets many prefixes pointing at the same
/// host share a single block.
///
/// An entry operates in one of three modes:
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
///
/// <para><b>Service-account mode</b> (<see cref="Username"/> and <see cref="Password"/> set): a fixed
/// credential sent as <c>Authorization: Basic base64(<see cref="Username"/>:<see cref="Password"/>)</c>
/// with nothing exchanged for it. This is the shape a container registry takes an account in - a Docker
/// Hub personal access token is the password, the account name the username - and note where it lands:
/// most registries ignore an <c>Authorization</c> header on <c>/v2/…</c> and accept an account only at
/// the token endpoint their challenge names, so for an OCI upstream this credential is also what
/// <see cref="RegistryTokenProvider"/> presents there, subject to <see cref="TokenRealms"/>. Set
/// <see cref="PublicUpstream"/> when the account only buys rate limit on a public registry.</para>
/// </summary>
public record UpstreamAuth
{
  // Upstream URL prefixes this auth applies to, matched scheme-agnostically against the upstream's
  // host[:port]/path (e.g. "repo.example.com/" or "repo.example.com/secure/"); the longest match wins.
  public required string[] UrlPrefixes { get; init; }

  public Uri? TokenEndpoint { get; init; }
  public string? ClientId { get; init; }
  public string? ClientSecret { get; init; }

  // Optional space-separated OAuth scopes, added to the token request when set.
  public string? Scope { get; init; }

  // Service-account mode (used instead of ClientId/TokenEndpoint/ClientSecret): a fixed Basic credential,
  // with no token exchange of its own. A Docker Hub PAT goes in Password, its account name in Username.
  public string? Username { get; init; }
  public string? Password { get; init; }

  /// <summary>
  /// Whether this upstream is public, i.e. the credential buys rate limit or throughput rather than
  /// access. The prefixes matching this entry then stay open to anonymous clients, instead of requiring
  /// the validated inbound client JWT that a private upstream's prefixes require (see
  /// <see cref="RemoteServers"/>) - a service account on Docker Hub must not turn a public mirror into a
  /// gated one.
  /// <para>Off by default, so a credential counts as an access grant until an entry says otherwise: the
  /// expensive mistake is serving private artifacts to anyone, not making a public pull authenticate.</para>
  /// </summary>
  public bool PublicUpstream { get; init; }

  // GitHub App mode (used instead of TokenEndpoint/ClientSecret). Supply the App's RSA private key inline
  // as PEM text (PrivateKey); ClientId is reused as the JWT issuer. InstallationId is optional — when
  // omitted it is auto-resolved if the App has exactly one installation. GitHubApiBaseUrl defaults to GitHub.com.
  public string? PrivateKey { get; init; }
  public string GitHubApiBaseUrl { get; init; } = "https://api.github.com";

  /// <summary>
  /// Extra token-endpoint URL prefixes this entry's credentials may be sent to, for an OCI upstream that
  /// issues its <c>WWW-Authenticate</c> challenge naming a realm on a different host (see
  /// <see cref="RegistryTokenProvider"/>). A realm on the upstream's own host is always allowed and needs
  /// no entry here.
  /// <para>An allowlist because the realm is a URL chosen by the response we are authenticating to: with
  /// no match the token is requested <b>anonymously</b> instead, so a public mirror needs no configuration
  /// and a spoofed or compromised upstream cannot redirect a service account to a collector of its
  /// choosing. Matched by literal, case-insensitive prefix, so include the scheme and a trailing slash.</para>
  /// </summary>
  public string[] TokenRealms { get; init; } = [];

  // True when this entry uses GitHub App auth (a private key is configured); ClientId then acts as the
  // JWT issuer rather than an OAuth client id.
  public bool IsGitHubApp => !string.IsNullOrEmpty(PrivateKey);

  // True when this entry is a fixed Basic credential rather than something exchanged for a token.
  public bool IsServiceAccount => !string.IsNullOrEmpty(Username);

  public override string ToString() => new StringBuilder()
    .Append(ClientId != null ? $"{nameof(ClientId)}: {ClientId} " : "")
    // The password is never rendered; the username identifies the account well enough to debug with.
    .Append(Username != null ? $"{nameof(Username)}: {Username} " : "")
    .Append(UrlPrefixes.Length > 0 ? $", {nameof(UrlPrefixes)}: {string.Join(" ", UrlPrefixes)}" : "")
    .Append(TokenEndpoint != null ? $", {nameof(TokenEndpoint)}: {TokenEndpoint}" : "")
    .Append(Scope != null ? $", {nameof(Scope)}:{Scope}" : "")
    .ToString();
}
