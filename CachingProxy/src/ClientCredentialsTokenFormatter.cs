using System;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.DPoP;
using MemoryPack;

namespace JetBrains.CachingProxy;

/// <summary>
/// MemoryPack-serializable surrogate for Duende's <see cref="ClientCredentialsToken"/>. The token's
/// members are Vogen value structs (string-backed) that MemoryPack can't serialize on its own, so
/// they are flattened to plain strings.
/// </summary>
[MemoryPackable]
public sealed partial record ClientCredentialsTokenSurrogate(
  string AccessToken,
  string? AccessTokenType,
  string? DPoPJsonWebKey,
  DateTimeOffset Expiration,
  string? Scope,
  string ClientId);

/// <summary>
/// Custom MemoryPack formatter that maps <see cref="ClientCredentialsToken"/> to/from
/// <see cref="ClientCredentialsTokenSurrogate"/>, so Duende's client-credentials token cache can use
/// the L2 (Redis) distributed cache. Without it, MemoryPack rejects the external, non-serializable
/// token type and Duende silently falls back to no caching (re-hitting the token endpoint each time).
/// </summary>
public sealed class ClientCredentialsTokenFormatter : MemoryPackFormatter<ClientCredentialsToken>
{
  public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref ClientCredentialsToken? value)
  {
    if (value is null)
    {
      writer.WriteValue<ClientCredentialsTokenSurrogate>(null);
      return;
    }

    writer.WriteValue(new ClientCredentialsTokenSurrogate(
      value.AccessToken.ToString(),
      value.AccessTokenType?.ToString(),
      value.DPoPJsonWebKey?.ToString(),
      value.Expiration,
      value.Scope?.ToString(),
      value.ClientId.ToString()));
  }

  public override void Deserialize(ref MemoryPackReader reader, scoped ref ClientCredentialsToken? value)
  {
    var surrogate = reader.ReadValue<ClientCredentialsTokenSurrogate>();
    if (surrogate is null)
    {
      value = null;
      return;
    }

    value = new ClientCredentialsToken
    {
      AccessToken = AccessToken.Parse(surrogate.AccessToken),
      AccessTokenType = surrogate.AccessTokenType is null ? null : AccessTokenType.Parse(surrogate.AccessTokenType),
      DPoPJsonWebKey = surrogate.DPoPJsonWebKey is null ? null : DPoPProofKey.Parse(surrogate.DPoPJsonWebKey),
      Expiration = surrogate.Expiration,
      Scope = surrogate.Scope is null ? null : Scope.Parse(surrogate.Scope),
      ClientId = ClientId.Parse(surrogate.ClientId),
    };
  }

  /// <summary>
  /// Registers this formatter with MemoryPack's global provider. Safe to call more than once;
  /// repeated registrations simply overwrite the previous one with an equivalent formatter.
  /// </summary>
  public static void Register() => MemoryPackFormatterProvider.Register(new ClientCredentialsTokenFormatter());
}
