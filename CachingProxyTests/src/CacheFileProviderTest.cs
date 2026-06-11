using System.IO;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class CacheFileProviderTest
{
  private static CacheFileProvider GetCacheFileProvider()
  {
    return new CacheFileProvider(new CachingProxyConfig
    {
      Prefixes =
      [
        "/a",
        "/b=a",
        "/c/d=a/",
      ],
      LocalCachePath = Path.GetTempPath()
    });
  }

  [Fact]
  public void ManglePath1()
  {
    var provider = GetCacheFileProvider();
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "d9", "6d" ,"d96d0bd13935d4ab082c410dea64c70bf2f926b75f3b487ac18c0e290ee8ac3a.jar"),
      provider.GetFileInfo("/a/a.jar").PhysicalPath);
  }

  [Fact]
  public void ManglePath2()
  {
    var provider = GetCacheFileProvider();
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "14", "40", "1440b34e1707076ba9c32fd06c18405254883be42d14cd240f237eaa3eb5960c"),
      provider.GetFileInfo("/a/a.jar/").PhysicalPath);
  }

  [Fact]
  public void ManglePath3()
  {
    var provider = GetCacheFileProvider();
    Assert.Null(provider.GetFileInfo("/tt.jar").PhysicalPath);
  }

  [Fact]
  public void ManglePath4()
  {
    var provider = GetCacheFileProvider();
    Assert.Null(provider.GetFileInfo("/../..").PhysicalPath);
  }

  [Fact]
  public void ManglePath_IsCaseSensitive()
  {
    // Upstreams are case-sensitive, so paths differing only in case must map to distinct cache files.
    var provider = GetCacheFileProvider();
    Assert.NotEqual(
      provider.GetFileInfo("/a/Foo.jar").PhysicalPath,
      provider.GetFileInfo("/a/foo.jar").PhysicalPath);
  }

  [Fact]
  public void ManglePathAlias()
  {
    var provider = GetCacheFileProvider();
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "14", "40", "1440b34e1707076ba9c32fd06c18405254883be42d14cd240f237eaa3eb5960c"),
      provider.GetFileInfo("/b/a.jar/").PhysicalPath);
  }

  [Fact]
  public void ManglePathAlias2()
  {
    var provider = GetCacheFileProvider();
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "14", "40", "1440b34e1707076ba9c32fd06c18405254883be42d14cd240f237eaa3eb5960c"),
      provider.GetFileInfo("/c/d/a.jar/").PhysicalPath);
  }

}
