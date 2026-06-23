using System;
using System.Linq;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class RemoteServersTest
{
  private static RemoteServers.RemoteServer[] Build(params string[] prefixes) =>
  [
    .. new RemoteServers(new CachingProxyConfig { Prefixes = [.. prefixes.Select(p => (CachingProxyPrefix)p)] })
      .Endpoints
      .Select(e => e.Metadata.GetMetadata<RemoteServers.RemoteServer>()!)
  ];

  [Fact]
  public void Plain_Prefix_Targets_Itself_Over_Https()
  {
    var server = Assert.Single(Build("/a"));
    Assert.Equal("/a", server.Prefix);
    Assert.Equal("https://a/", server.RemoteUri.ToString());
  }

  [Fact]
  public void Alias_Maps_Prefix_To_Different_Target()
  {
    var server = Assert.Single(Build("/b=a"));
    Assert.Equal("/b", server.Prefix);
    Assert.Equal("https://a/", server.RemoteUri.ToString());
  }

  [Fact]
  public void Aliases_Resolve_To_The_Same_Upstream_Key()
  {
    // /a, /b=a and /c/d=a/ all point at https://a/, so the same artifact must yield the same key
    // regardless of which prefix was used to reach it.
    var servers = Build("/a", "/b=a", "/c/d=a/");
    Assert.Equal(new[] { "/a", "/b", "/c/d" }, servers.Select(s => s.Prefix.Value!).ToArray());
    var keys = servers.Select(s => s.GetUpstreamUri("a.jar").ManglePath()).Distinct();
    Assert.Equal("d9/6d/d96d0bd13935d4ab082c410dea64c70bf2f926b75f3b487ac18c0e290ee8ac3a", Assert.Single(keys));
  }

  [Fact]
  public void Absolute_Target_Is_Preserved()
  {
    var server = Assert.Single(Build("/p=http://example.org/sub"));
    Assert.Equal("http://example.org/sub/", server.RemoteUri.ToString());
  }

  [Fact]
  public void Upstream_Auth_Matches_By_Longest_Url_Prefix()
  {
    var hostWide = new UpstreamAuth { UrlPrefix = "https://repo.example.com/", TokenEndpoint = new Uri("https://repo.example.com/"), ClientId = "host", ClientSecret = "" };
    var pathScoped = new UpstreamAuth { UrlPrefix = "https://repo.example.com/secure/", TokenEndpoint = new Uri("https://repo.example.com/"), ClientSecret = "", ClientId = "scoped" };

    var config = new CachingProxyConfig
    {
      Prefixes =
      [
        "/a=repo.example.com/maven",        // → host-wide entry
        "/b=repo.example.com/secure/maven", // → longer, more specific entry wins
        "/c=other.example.com",             // → no match
      ],
      UpstreamAuth = [hostWide, pathScoped],
      // A matched UpstreamAuth requires InboundAuth to be configured (see RemoteServers); supply a
      // minimal block so construction succeeds and we can assert the matching itself.
      InboundAuth = new CachingProxyConfig.InboundAuthConfig { Issuer = "i", Audience = "a", JwksUrl = new Uri("https://issuer.example.com/jwks.json") },
    };

    var servers = new RemoteServers(config).Endpoints
      .Select(e => e.Metadata.GetMetadata<RemoteServers.RemoteServer>()!)
      .ToArray();

    Assert.Same(hostWide, servers.Single(s => s.Prefix == "/a").Auth);
    Assert.Same(pathScoped, servers.Single(s => s.Prefix == "/b").Auth);
    Assert.Null(servers.Single(s => s.Prefix == "/c").Auth);
  }
}
