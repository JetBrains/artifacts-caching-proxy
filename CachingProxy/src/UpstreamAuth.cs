using System;

namespace JetBrains.CachingProxy;

/// <summary>
/// OAuth 2.0 client-credentials authentication for one or more upstream sources, configured
/// separately from <see cref="CachingProxyPrefix"/> and matched to an upstream by the longest
/// <see cref="UrlPrefix"/> of its resolved URL (see <see cref="RemoteServers"/>). This lets many
/// prefixes pointing at the same host share a single credential block.
///
/// The proxy fetches an access token from <see cref="TokenEndpoint"/> (the token request itself
/// authenticates the client with HTTP Basic, the library default) and sends it to the upstream as
/// <c>Authorization: Basic base64(<see cref="ClientId"/>:access_token)</c> — the client id is the
/// Basic username and the token is the password.
/// </summary>
public record UpstreamAuth
{
  // Upstream URL prefix this auth applies to (e.g. "https://repo.example.com/"); longest match wins.
  // Also used as the token-management client name, so it must be unique across entries.
  public required string UrlPrefix { get; init; }

  public required Uri TokenEndpoint { get; init; }
  public required string ClientId { get; init; }
  public required string ClientSecret { get; init; }

  // Optional space-separated OAuth scopes, added to the token request when set.
  public string? Scope { get; init; }
}
