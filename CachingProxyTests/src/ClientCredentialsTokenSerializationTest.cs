using System;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.DPoP;
using Xunit;
using ZiggyCreatures.Caching.Fusion.Serialization;
using ZiggyCreatures.Caching.Fusion.Serialization.CysharpMemoryPack;

namespace JetBrains.CachingProxy.Tests;

public class ClientCredentialsTokenSerializationTest
{
  private readonly IFusionCacheSerializer _serializer;

  public ClientCredentialsTokenSerializationTest()
  {
    // The same registration the app performs when L2 is wired; required so MemoryPack can handle
    // Duende's non-serializable ClientCredentialsToken via the surrogate formatter.
    ClientCredentialsTokenFormatter.Register();
    _serializer = new FusionCacheCysharpMemoryPackSerializer();
  }

  private ClientCredentialsToken RoundTrip(ClientCredentialsToken original)
  {
    var bytes = _serializer.Serialize(original);
    var result = _serializer.Deserialize<ClientCredentialsToken>(bytes);
    Assert.NotNull(result);
    return result!;
  }

  [Fact]
  public void RoundTrip_PreservesAllFields()
  {
    var expiration = new DateTimeOffset(2026, 7, 1, 17, 46, 47, TimeSpan.Zero);
    var original = new ClientCredentialsToken
    {
      AccessToken = AccessToken.Parse("abc.def.ghi"),
      AccessTokenType = AccessTokenType.Parse("Bearer"),
      DPoPJsonWebKey = DPoPProofKey.Parse("{\"kty\":\"EC\"}"),
      Expiration = expiration,
      Scope = Scope.Parse("read write"),
      ClientId = ClientId.Parse("d44b3be4-ebe7-4503-b14b-686cd9744ece"),
    };

    var result = RoundTrip(original);

    Assert.Equal("abc.def.ghi", result.AccessToken.ToString());
    Assert.Equal("Bearer", result.AccessTokenType?.ToString());
    Assert.Equal("{\"kty\":\"EC\"}", result.DPoPJsonWebKey?.ToString());
    Assert.Equal(expiration, result.Expiration);
    Assert.Equal("read write", result.Scope?.ToString());
    Assert.Equal("d44b3be4-ebe7-4503-b14b-686cd9744ece", result.ClientId.ToString());
  }

  [Fact]
  public void RoundTrip_PreservesNullOptionalFields()
  {
    // The common client-credentials case: no token type, DPoP key, or scope.
    var expiration = new DateTimeOffset(2026, 7, 1, 17, 51, 47, TimeSpan.Zero);
    var original = new ClientCredentialsToken
    {
      AccessToken = AccessToken.Parse("token-only"),
      AccessTokenType = null,
      DPoPJsonWebKey = null,
      Expiration = expiration,
      Scope = null,
      ClientId = ClientId.Parse("client-1"),
    };

    var result = RoundTrip(original);

    Assert.Equal("token-only", result.AccessToken.ToString());
    Assert.Null(result.AccessTokenType);
    Assert.Null(result.DPoPJsonWebKey);
    Assert.Equal(expiration, result.Expiration);
    Assert.Null(result.Scope);
    Assert.Equal("client-1", result.ClientId.ToString());
  }
}
