using System.IO;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy
{
  public class CacheFileProvider: IFileProvider
  {
    private readonly PhysicalFileProvider myPhysicalFileProvider;

    public CacheFileProvider(string cacheDirectory)
    {
      myPhysicalFileProvider = new PhysicalFileProvider(cacheDirectory);
    }

    public IFileInfo GetFileInfo(string subpath)
    {
      return myPhysicalFileProvider.GetFileInfo(ManglePath(subpath));
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
      return myPhysicalFileProvider.GetDirectoryContents(ManglePath(subpath));
    }

    public IChangeToken Watch(string filter)
    {
      throw new System.NotSupportedException();
    }

    /// <summary>
    /// Change sub-path upon converting to local file system path to handle hierarchy-conflicts like
    /// caching both a/a.jar and a/b/c/c.jar
    /// </summary>
    private static string ManglePath(string subpath)
    {
      var trimmed = subpath.TrimEnd(Path.DirectorySeparatorChar);
      var lastSeparator = trimmed.LastIndexOf(Path.DirectorySeparatorChar);
      return lastSeparator < 0
        ? $"cache-{trimmed}"
        : $"{trimmed.Substring(0, lastSeparator + 1)}cache-{trimmed.Substring(lastSeparator + 1)}";
    }
  }
}
