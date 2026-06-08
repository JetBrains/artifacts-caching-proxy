using System.IO;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class CacheFileProviderTest
{
  [Fact]
  public void ManglePath1()
  {
    var provider = new CacheFileProvider(Path.GetTempPath());
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "5a", "19" ,"5a190ba5c5133e77b2e641b34495ba7d0d6cb9cfc21003ddd21616c48f1dd5f2.jar"),
      provider.GetFileInfo(Path.Combine("a", "a.jar")).PhysicalPath);
  }

  [Fact]
  public void ManglePath2()
  {
    var provider = new CacheFileProvider(Path.GetTempPath());
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "f5", "31", "f531967754805be0dca464ca4b138586fd9955af5d6a8d70aa7da286e380e38a"),
      provider.GetFileInfo(Path.Combine("a", "a.jar") + Path.DirectorySeparatorChar).PhysicalPath);
  }

  [Fact]
  public void ManglePath3()
  {
    var provider = new CacheFileProvider(Path.GetTempPath());
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "cc", "1e", "cc1e9f54ca1b8b10ec9e11489da9b04ad7408c2f8839f04ccbbb42c23fde60eb.jar"),
      provider.GetFileInfo("tt.jar").PhysicalPath);
  }

  [Fact]
  public void ManglePath4()
  {
    var provider = new CacheFileProvider(Path.GetTempPath());
    Assert.Equal(
      Path.Combine(Path.GetTempPath(), "9f", "32", "9f32d3d201e48581cce89ae5b97e51aa778b034a5546bd81e82264c4b5dd9d84"),
      provider.GetFileInfo(Path.Combine("..", "..")).PhysicalPath);
  }
}
