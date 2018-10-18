using System.IO;
using Xunit;

namespace JetBrains.CachingProxy.Tests
{
  public class CacheFileProviderTest
  {
    [Fact]
    public void MangePath1()
    {
      var provider = new CacheFileProvider(Path.GetTempPath());
      Assert.Equal(
        Path.Combine(Path.GetTempPath(), "a", "cache-a.jar"),
        provider.GetFileInfo(Path.Combine("a", "a.jar")).PhysicalPath);
    }

    [Fact]
    public void MangePath2()
    {
      var provider = new CacheFileProvider(Path.GetTempPath());
      Assert.Equal(
        Path.Combine(Path.GetTempPath(), "a", "cache-a.jar"),
        provider.GetFileInfo(Path.Combine("a", "a.jar") + Path.DirectorySeparatorChar).PhysicalPath);
    }

    [Fact]
    public void MangePath3()
    {
      var provider = new CacheFileProvider(Path.GetTempPath());
      Assert.Equal(
        Path.Combine(Path.GetTempPath(), "cache-tt.jar"),
        provider.GetFileInfo("tt.jar").PhysicalPath);
    }

    [Fact]
    public void MangePath4()
    {
      var provider = new CacheFileProvider(Path.GetTempPath());
      var fileInfo = provider.GetFileInfo(Path.Combine("..", ".."));
      Assert.Null(fileInfo.PhysicalPath);
    }
  }
}
