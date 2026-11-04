using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZiggyCreatures.Caching.Fusion;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// CodeArtifact mode: the endpoint host names the domain, the default AWS credential chain mints the token.
// Covers host parsing, what startup rejects, the header shape and token caching.
public class UpstreamCodeArtifactTest
{
  private const string Endpoint = "cache-redirector-840632698608.d.codeartifact.eu-west-1.amazonaws.com";
  private const string UrlPrefix = Endpoint + "/nuget/ultimate/";

  [Theory]
  [InlineData(UrlPrefix, "cache-redirector", "840632698608", "eu-west-1")]
  // Digits and hyphens in the domain name: the owner is the last 12-digit group before .d.codeartifact.
  [InlineData("my-123456789012-domain-840632698608.d.codeartifact.eu-central-1.amazonaws.com/npm/x/",
    "my-123456789012-domain", "840632698608", "eu-central-1")]
  [InlineData("cache-redirector-840632698608.d.codeartifact.eu-west-1.amazonaws.com:443/nuget/ultimate/",
    "cache-redirector", "840632698608", "eu-west-1")]
  [InlineData("cache-redirector-840632698608.d.codeartifact.eu-west-1.amazonaws.com",
    "cache-redirector", "840632698608", "eu-west-1")]
  public void The_Domain_Is_Parsed_From_The_Endpoint_Host(string urlPrefix, string name, string owner, string region) =>
    Assert.Equal(new CodeArtifactDomain(name, owner, region), CodeArtifactDomain.FromUrlPrefix(urlPrefix));

  [Theory]
  [InlineData("packages.jetbrains.team/nuget/p/net/build/")]
  [InlineData("cache-redirector.d.codeartifact.eu-west-1.amazonaws.com/nuget/ultimate/")]
  [InlineData("cache-redirector-84063269860.d.codeartifact.eu-west-1.amazonaws.com/nuget/ultimate/")]
  [InlineData("cache-redirector-840632698608.d.codeartifact.eu-west-1.amazonaws.com.example.com/nuget/")]
  [InlineData("example.com/" + UrlPrefix)]
  public void Other_Hosts_Are_No_CodeArtifact_Endpoints(string urlPrefix) =>
    Assert.Null(CodeArtifactDomain.FromUrlPrefix(urlPrefix));

  private static void Configure(string[] urlPrefixes, params (string Key, string Value)[] settings)
  {
    var values = new Dictionary<string, string?>();
    for (var i = 0; i < urlPrefixes.Length; i++) values[$"UpstreamAuth:ca:UrlPrefixes:{i}"] = urlPrefixes[i];
    foreach (var (key, value) in settings) values["UpstreamAuth:ca:" + key] = value;
    new ServiceCollection().AddUpstreamAuth(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
  }

  [Fact]
  public void An_Endpoint_Alone_Is_A_Complete_Entry() => Configure([UrlPrefix]);

  [Theory]
  [InlineData("ClientId", "svc-proxy")]
  [InlineData("Username", "svc")]
  [InlineData("Password", "pat")]
  [InlineData("PrivateKey", "-----BEGIN RSA PRIVATE KEY-----")]
  public void Mixing_CodeArtifact_With_Another_Mode_Is_Rejected(string key, string value)
  {
    var exception = Assert.Throws<ArgumentException>(() => Configure([UrlPrefix], (key, value)));
    Assert.Contains("authenticates one way only", exception.Message);
  }

  [Fact]
  public void Mixing_Endpoints_With_Other_Prefixes_Is_Rejected()
  {
    var exception = Assert.Throws<ArgumentException>(() => Configure([UrlPrefix, "packages.jetbrains.team/nuget/p/"]));
    Assert.Contains("mixes CodeArtifact endpoints with other UrlPrefixes", exception.Message);
  }

  [Theory]
  [InlineData("other-840632698608.d.codeartifact.eu-west-1.amazonaws.com/nuget/ultimate/")]
  [InlineData("cache-redirector-111122223333.d.codeartifact.eu-west-1.amazonaws.com/nuget/ultimate/")]
  [InlineData("cache-redirector-840632698608.d.codeartifact.eu-north-1.amazonaws.com/nuget/ultimate/")]
  public void Spanning_Domains_Is_Rejected(string otherPrefix)
  {
    var exception = Assert.Throws<ArgumentException>(() => Configure([UrlPrefix, otherPrefix]));
    Assert.Contains("spans CodeArtifact domains", exception.Message);
  }

  [Fact]
  public void An_Endpoint_Counts_As_A_Credential()
  {
    // So its prefixes are gated like every other credentialed upstream.
    Assert.True(new UpstreamAuth { UrlPrefixes = [UrlPrefix] }.HasCredential);
  }

  [Fact]
  public void The_Domain_Is_Rendered()
  {
    Assert.Contains("CodeArtifact: cache-redirector-840632698608", new UpstreamAuth { UrlPrefixes = [UrlPrefix] }.ToString());
  }

  private sealed class FakeTokenClient(FakeTimeProvider time) : ICodeArtifactTokenClient
  {
    public readonly List<CodeArtifactDomain> Calls = [];

    public Task<(string Token, DateTimeOffset ExpiresAt)> GetAuthorizationTokenAsync(CodeArtifactDomain domain, CancellationToken ct)
    {
      Calls.Add(domain);
      // Base64 padding and '+'/'/' on purpose: the header must carry the token verbatim.
      return Task.FromResult(($"tok+/={Calls.Count}", time.GetUtcNow() + TimeSpan.FromHours(12)));
    }
  }

  // FusionCache takes expirations from the system clock, the memory cache compares them on the fake one, so
  // the fake starts at the real now (see ResponseCacheTest).
  private static (UpstreamAuthorizationProvider Provider, FakeTokenClient Client, FakeTimeProvider Time) CreateProvider()
  {
    var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var client = new FakeTokenClient(time);
    var cache = new FusionCache(new FusionCacheOptions(),
      new MemoryCache(new MemoryCacheOptions { Clock = new TimeProviderClock(time) }));
    var tokens = new CodeArtifactTokenProvider(client, cache, time, NullLogger<CodeArtifactTokenProvider>.Instance);
    // null GitHub App provider and Duende manager: CodeArtifact mode resolves neither.
    return (new UpstreamAuthorizationProvider(null!, null, tokens), client, time);
  }

  [Fact]
  public async Task The_Token_Is_The_Basic_Password_Of_User_Aws()
  {
    var (provider, _, _) = CreateProvider();

    var header = await provider.GetAuthorizationHeaderAsync(new UpstreamAuth { UrlPrefixes = [UrlPrefix] }, CancellationToken.None);

    Assert.NotNull(header);
    Assert.Equal("Basic", header.Scheme);
    Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("aws:tok+/=1")), header.Parameter);
  }

  [Fact]
  public async Task The_Token_Is_Minted_For_The_Endpoint_Domain()
  {
    var (provider, client, _) = CreateProvider();

    await provider.GetAuthorizationHeaderAsync(new UpstreamAuth { UrlPrefixes = [UrlPrefix] }, CancellationToken.None);

    Assert.Equal(new CodeArtifactDomain("cache-redirector", "840632698608", "eu-west-1"), Assert.Single(client.Calls));
  }

  [Fact]
  public async Task The_Token_Is_Cached_And_Reminted_Before_Expiry()
  {
    var (provider, client, time) = CreateProvider();
    var auth = new UpstreamAuth { UrlPrefixes = [UrlPrefix] };

    var first = await provider.GetAuthorizationHeaderAsync(auth, CancellationToken.None);
    time.Advance(TimeSpan.FromHours(11));
    var cached = await provider.GetAuthorizationHeaderAsync(auth, CancellationToken.None);
    Assert.Equal(first!.Parameter, cached!.Parameter);
    Assert.Single(client.Calls);

    // Inside the 30-minute refresh window of the 12-hour token: minted again while the old one still works.
    time.Advance(TimeSpan.FromMinutes(40));
    var refreshed = await provider.GetAuthorizationHeaderAsync(auth, CancellationToken.None);
    Assert.Equal(2, client.Calls.Count);
    Assert.NotEqual(first.Parameter, refreshed!.Parameter);
  }
}
