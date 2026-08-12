using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// Service-account mode: a fixed Username/Password (a container-registry PAT) with no token exchange of
// its own. Covers what the startup validation rejects and the header the provider builds from it.
public class UpstreamServiceAccountTest
{
  private static void Configure(params (string Key, string Value)[] settings)
  {
    var values = new Dictionary<string, string?> { ["UpstreamAuth:reg:UrlPrefixes:0"] = "registry-1.docker.io/v2/" };
    foreach (var (key, value) in settings) values["UpstreamAuth:reg:" + key] = value;
    new ServiceCollection().AddUpstreamAuth(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
  }

  [Fact]
  public void A_Service_Account_Needs_No_Token_Endpoint()
  {
    // The whole point of the mode: nothing is exchanged, so none of the client-credentials requirements
    // (TokenEndpoint, ClientSecret) apply and startup must not demand them.
    Configure(("Username", "svc"), ("Password", "pat"));
  }

  [Theory]
  [InlineData("ClientId", "svc-proxy")]
  [InlineData("PrivateKey", "-----BEGIN RSA PRIVATE KEY-----")]
  public void Mixing_A_Service_Account_With_Another_Mode_Is_Rejected(string key, string value)
  {
    var exception = Assert.Throws<ArgumentException>(() =>
      Configure(("Username", "svc"), ("Password", "pat"), (key, value)));
    Assert.Contains("authenticates one way only", exception.Message);
  }

  [Theory]
  [InlineData("Username", "svc")]
  [InlineData("Password", "pat")]
  public void Half_A_Credential_Is_Rejected(string key, string value)
  {
    // This is what an unresolved {{resolve:secretsmanager:…}} reference looks like, and left alone it
    // degrades to an anonymous registry token: pulls keep working until the anonymous rate limit bites,
    // with nothing in the response to say why.
    var exception = Assert.Throws<ArgumentException>(() => Configure((key, value)));
    Assert.Contains("only one of Username/Password", exception.Message);
  }

  [Fact]
  public void No_Credential_At_All_Is_Fine()
  {
    // An anonymous OCI upstream still needs an entry when it declares TokenRealms.
    Configure(("TokenRealms:0", "https://auth.docker.io/"));
  }

  [Fact]
  public async Task The_Account_Is_Encoded_As_Basic_Verbatim()
  {
    var auth = new UpstreamAuth
    {
      UrlPrefixes = ["registry-1.docker.io/v2/"], Username = "svc", Password = "pat",
    };

    // null dependencies: service-account mode resolves neither the GitHub App provider nor the Duende
    // token manager, which is the point of dispatching on it first.
    var header = await new UpstreamAuthorizationProvider(null!, null).GetAuthorizationHeaderAsync(auth, CancellationToken.None);

    Assert.NotNull(header);
    Assert.Equal("Basic", header.Scheme);
    Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("svc:pat")), header.Parameter);
  }

  [Fact]
  public void The_Password_Is_Never_Rendered()
  {
    // UpstreamAuth is logged per prefix at startup (see RemoteServers).
    var rendered = new UpstreamAuth
    {
      UrlPrefixes = ["registry-1.docker.io/v2/"], Username = "svc", Password = "dckr_pat_secret",
    }.ToString();

    Assert.Contains("svc", rendered);
    Assert.DoesNotContain("dckr_pat_secret", rendered);
  }
}
