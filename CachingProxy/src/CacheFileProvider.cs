using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

      var withoutGzipSuffix = myPhysicalFileProvider.GetFileInfo(ManglePath(subpath));
      return withoutGzipSuffix;
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => throw new NotSupportedException();

    public string? GetFutureCacheFileLocation(string subpath, string? contentEncoding) =>
      myPhysicalFileProvider.GetFileInfo(ManglePath(subpath, GetContentEncodingCacheFileSuffix(contentEncoding))).PhysicalPath;

    public string? GetContentEncoding(IFileInfo fileInfo) =>
      fileInfo.PhysicalPath?.EndsWith(ourGzippedContentSuffix) ?? false ? "gzip" : null;

    public IChangeToken Watch(string filter) => throw new NotSupportedException();

    /// <summary>
    /// Change sub-path upon converting to a local file system path to handle hierarchy-conflicts like
    /// caching both a/a.jar and a/b/c/c.jar, or a and a/b.jar
    /// </summary>
    private static string ManglePath(string subpath, string? contentEncodingSuffix = null)
    {
      var maxBytes = Encoding.UTF8.GetMaxByteCount(subpath.Length);

      byte[]? rented = null;
      var buffer = maxBytes <= 512
        ? stackalloc byte[512]
        : rented = ArrayPool<byte>.Shared.Rent(maxBytes);

      try
      {
        var written = Encoding.UTF8.GetBytes(subpath, buffer);
        // normalize in-place: lowercase ASCII letters and force forward-slash delimiter
        for (var i = 0; i < written; i++)
        {
          if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar && buffer[i] == (byte)Path.DirectorySeparatorChar)
          {
            buffer[i] = (byte)Path.AltDirectorySeparatorChar;
          }
          else if ((uint)(buffer[i] - 'A') < 26u)
          {
            buffer[i] |= 0x20;
          }
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(buffer[..written]));
        return $"{hash[..2]}/{hash[2..4]}/{hash}{Path.GetExtension(subpath)}{contentEncodingSuffix}";
      }
      finally
      {
        if (rented != null)
          ArrayPool<byte>.Shared.Return(rented);
      }
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
