using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// Which inbound credential a configuration declares, as AddInboundAuth reads it. The deployed proxy
// declares the redirect signature alone - the client JWT is validated by the redirector, the only layer
// that sees one on a redirected request and the only one that checks it against Space for revocation
// (MRI-4847) - so the JWT parameters are optional. But only as a set, and only with a signature to take
// their place: the two ways to end up accepting nothing, or to end up validating less than intended
// because one setting went missing, are startup errors.
public class InboundAuthModeTest
{
  private const string SignatureKey = "super-secret-shared-hmac-key-32bytes";

  private static void Configure(params (string Key, string Value)[] settings)
  {
    var values = new Dictionary<string, string?>();
    foreach (var (key, value) in settings) values["InboundAuth:" + key] = value;
    new ServiceCollection().AddInboundAuth(
      new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new TestHostEnvironment());
  }

  [Fact]
  public void The_Signature_Alone_Is_A_Complete_Configuration()
  {
    // The deployed shape: no Issuer/Audiences/JwksUrl at all, so no JWT is accepted here.
    Configure(("RedirectSignature:Key", SignatureKey));
  }

  [Fact]
  public void The_Jwt_Alone_Is_Still_A_Complete_Configuration()
  {
    // Unchanged for a deployment with no redirector in front of it.
    Configure(
      ("Issuer", "https://jetbrains.team"),
      ("Audiences:0", "cache-redirector"),
      ("JwksUrl", "https://jetbrains.team/oauth/jwks.json"));
  }

  [Fact]
  public void Neither_Credential_Is_Rejected()
  {
    // What a dropped RedirectSignatureKey secret looks like: an InboundAuth section that accepts nothing,
    // so every gated prefix 401s with nothing able to unblock it.
    var exception = Assert.Throws<ArgumentException>(() => Configure(("RequireExpiration", "false")));
    Assert.Contains("neither the JWT parameters", exception.Message);
  }

  [Theory]
  [InlineData("Issuer", "https://jetbrains.team")]
  [InlineData("Audiences:0", "cache-redirector")]
  [InlineData("JwksUrl", "https://jetbrains.team/oauth/jwks.json")]
  public void Half_A_Jwt_Section_Is_Rejected_Rather_Than_Ignored(string key, string value)
  {
    // One JWT parameter left on its own - a dropped env var, an unresolved secret reference - must not
    // read as "signature only", which would turn a misconfiguration into a silent change of what the
    // proxy validates.
    Assert.Throws<ArgumentException>(() => Configure(("RedirectSignature:Key", SignatureKey), (key, value)));
  }

  private sealed class TestHostEnvironment : IHostEnvironment
  {
    public string ApplicationName { get; set; } = "artifacts-caching-proxy";
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
  }
}
