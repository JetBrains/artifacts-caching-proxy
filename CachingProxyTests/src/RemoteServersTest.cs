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
    var keys = servers.Select(s => s.GetUpstreamUriKey("a.jar")).Distinct();
    Assert.Equal("a/a.jar", Assert.Single(keys));
  }

  [Fact]
  public void Absolute_Target_Is_Preserved()
  {
    var server = Assert.Single(Build("/p=http://example.org/sub"));
    Assert.Equal("http://example.org/sub/", server.RemoteUri.ToString());
  }
}
