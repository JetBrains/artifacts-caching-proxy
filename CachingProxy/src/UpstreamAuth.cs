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
/// <para><b>Redirect-only / external-auth mode</b> (<see cref="ClientId"/> empty): the proxy holds no
/// upstream credentials. The matched prefix is always 307-redirected to the origin (e.g. CloudFront),
/// which performs its own per-user authorization and serves the content directly — so the proxy neither
/// caches it nor gates it with inbound auth. Only <see cref="UrlPrefix"/> is needed;
/// <see cref="TokenEndpoint"/>/<see cref="ClientSecret"/> are ignored.</para>
/// </summary>
public record UpstreamAuth
{
  // Upstream URL prefix this auth applies to (e.g. "https://repo.example.com/"); the longest match wins.
  // Also used as the token-management client name, so it must be unique across entries.
  public required string UrlPrefix { get; init; }

  // When set, the entry is in credential mode and TokenEndpoint/ClientSecret are required; when empty,
  // the entry is redirect-only (origin authorizes) — see the record summary and HasCredentials.
  public Uri? TokenEndpoint { get; init; }
  public string? ClientId { get; init; }
  public string? ClientSecret { get; init; }

  // Optional space-separated OAuth scopes, added to the token request when set.
  public string? Scope { get; init; }

  // True when this entry carries upstream OAuth credentials (credential mode). False means redirect-only
  // / external-auth mode: the proxy always redirects to the origin and does not require inbound auth.
  public bool HasCredentials => !string.IsNullOrEmpty(ClientId);
}
