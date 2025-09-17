using System;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy
{
  public class CacheFileProvider(string cacheDirectory) : IFileProvider
  {
    private static readonly string ourGzippedContentSuffix = "-gzip-Ege4dHyCEA7IM";

    private readonly PhysicalFileProvider myPhysicalFileProvider = new(cacheDirectory);

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

    public string? GetFutureCacheFileLocation(string subpath, string? contentEncoding)
    {
      var fileInfo = myPhysicalFileProvider.GetFileInfo(ManglePath(subpath, GetContentEncodingCacheFileSuffix(contentEncoding)));
      return fileInfo.PhysicalPath;
    }

    public string? GetContentEncoding(IFileInfo fileInfo)
    {
      return fileInfo.PhysicalPath?.EndsWith(ourGzippedContentSuffix) ?? false ? "gzip" : null;
    }

    public IChangeToken Watch(string filter)
    {
      throw new NotSupportedException();
    }

    /// <summary>
    /// Change sub-path upon converting to a local file system path to handle hierarchy-conflicts like
    /// caching both a/a.jar and a/b/c/c.jar
    /// </summary>
    private static string ManglePath(string subpath, string contentEncodingSuffix)
    {
      var trimmed = subpath.Replace('\\', '/').TrimEnd('/');
      var lastSeparator = trimmed.LastIndexOf('/');
      return lastSeparator < 0
        ? $"cache-{trimmed}"
        : $"{trimmed[..(lastSeparator + 1)]}cache-{trimmed[(lastSeparator + 1)..]}{contentEncodingSuffix}";
    }

    private static string GetContentEncodingCacheFileSuffix(string? contentEncoding) =>
      contentEncoding switch
      {
        null => "",
        "gzip" => ourGzippedContentSuffix,
        _ => throw new ArgumentException($"Unsupported Content-Encoding: {contentEncoding}")
      };
  }
}
