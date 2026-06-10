using System.IO;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class CacheFileProviderTest
{
  [Fact]
  public void ManglePath1()
  {
    var provider = new CacheFileProvider(new CachingProxyConfig { LocalCachePath = Path.GetTempPath() });
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "d9", "6d" ,"d96d0bd13935d4ab082c410dea64c70bf2f926b75f3b487ac18c0e290ee8ac3a.jar"),
      provider.GetFileInfo(Path.Combine("a", "a.jar")).PhysicalPath);
  }

  [Fact]
  public void ManglePath2()
  {
    var provider = new CacheFileProvider(new CachingProxyConfig { LocalCachePath = Path.GetTempPath() });
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "14", "40", "1440b34e1707076ba9c32fd06c18405254883be42d14cd240f237eaa3eb5960c"),
      provider.GetFileInfo(Path.Combine("a", "a.jar") + Path.DirectorySeparatorChar).PhysicalPath);
  }

  [Fact]
  public void ManglePath3()
  {
    var provider = new CacheFileProvider(new CachingProxyConfig { LocalCachePath = Path.GetTempPath() });
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "cc", "1e", "cc1e9f54ca1b8b10ec9e11489da9b04ad7408c2f8839f04ccbbb42c23fde60eb.jar"),
      provider.GetFileInfo("tt.jar").PhysicalPath);
  }

  [Fact]
  public void ManglePath4()
  {
    var provider = new CacheFileProvider(new CachingProxyConfig { LocalCachePath = Path.GetTempPath() });
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "0c", "fd", "0cfd1c96cbc0c0e025888750a8b75d7d507ab83eaef58650f1023579f03ca306"),
      provider.GetFileInfo(Path.Combine("..", "..")).PhysicalPath);
  }

  [Fact]
  public void ManglePath_IsCaseSensitive()
  {
    // Upstreams are case-sensitive, so paths differing only in case must map to distinct cache files.
    var provider = new CacheFileProvider(new CachingProxyConfig { LocalCachePath = Path.GetTempPath() });
    Assert.NotEqual(
      provider.GetFileInfo(Path.Combine("a", "Foo.jar")).PhysicalPath,
      provider.GetFileInfo(Path.Combine("a", "foo.jar")).PhysicalPath);
  }
}
