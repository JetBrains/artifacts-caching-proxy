using System;
using JetBrains.Annotations;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy
{
  public class CacheFileProvider: IFileProvider
  {
    private static readonly string ourGzippedContentSuffix = "-gzip-Ege4dHyCEA7IM";

    private readonly PhysicalFileProvider myPhysicalFileProvider;

    public CacheFileProvider(string cacheDirectory)
    {
      myPhysicalFileProvider = new PhysicalFileProvider(cacheDirectory);
    }

    public IFileInfo GetFileInfo(string subpath)
    {
      var withGzipSuffix = myPhysicalFileProvider.GetFileInfo(ManglePath(subpath, ourGzippedContentSuffix));
      if (withGzipSuffix.Exists)
        return withGzipSuffix;

      var withoutGzipSuffix = myPhysicalFileProvider.GetFileInfo(ManglePath(subpath, ""));
      return withoutGzipSuffix;
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
      return myPhysicalFileProvider.GetDirectoryContents(ManglePath(subpath, ""));
    }

    public string GetFutureCacheFileLocation(string subpath, string contentEncoding)
    {
      var fileInfo = myPhysicalFileProvider.GetFileInfo(ManglePath(subpath, GetContentEncodingCacheFileSuffix(contentEncoding)));
      return fileInfo?.PhysicalPath;
    }

    [CanBeNull]
    public string GetContentEncoding(IFileInfo fileInfo)
    {
      return fileInfo.PhysicalPath.EndsWith(ourGzippedContentSuffix) ? "gzip" : null;
    }

    public IChangeToken Watch(string filter)
    {
      throw new NotSupportedException();
    }

    /// <summary>
    /// Change sub-path upon converting to local file system path to handle hierarchy-conflicts like
    /// caching both a/a.jar and a/b/c/c.jar
    /// </summary>
    private static string ManglePath(string subpath, string contentEncodingSuffix)
    {
      var trimmed = subpath.Replace('\\', '/').TrimEnd('/');
      var lastSeparator = trimmed.LastIndexOf('/');
      return lastSeparator < 0
        ? $"cache-{trimmed}"
        : $"{trimmed.Substring(0, lastSeparator + 1)}cache-{trimmed.Substring(lastSeparator + 1)}{contentEncodingSuffix}";
    }

    [NotNull]
    private static string GetContentEncodingCacheFileSuffix([CanBeNull] string contentEncoding) =>
      contentEncoding switch
      {
        null => "",
        "gzip" => ourGzippedContentSuffix,
        _ => throw new ArgumentException($"Unsupported Content-Encoding: {contentEncoding}")
      };
  }
}
