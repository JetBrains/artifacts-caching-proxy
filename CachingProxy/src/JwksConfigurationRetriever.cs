using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace JetBrains.CachingProxy;

// Fetches a raw JSON Web Key Set (JWKS) document and exposes its signing keys as an
// OpenIdConnectConfiguration, so a JWKS-only endpoint (no OIDC discovery document) can drive
// JwtBearerOptions.ConfigurationManager. Going through ConfigurationManager gives key caching plus
// automatic refresh — including an on-demand refresh when signature validation fails, which is how
// key rotation is picked up without a redeploy.
internal sealed class JwksConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
  public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
    string address, IDocumentRetriever retriever, CancellationToken cancel)
  {
    var json = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
    var configuration = new OpenIdConnectConfiguration();
    foreach (var key in new JsonWebKeySet(json).GetSigningKeys())
      configuration.SigningKeys.Add(key);
    return configuration;
  }
}
