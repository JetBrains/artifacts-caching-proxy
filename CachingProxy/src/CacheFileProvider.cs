using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JetBrains.CachingProxy;

public static class CacheFileProvider
{
  private static readonly string ourGzippedContentSuffix = "-gzip-Ege4dHyCEA7IM";

  // Suffix of the companion "stamp" file whose LastWriteTime records when we stored a cache file —
  // the anchor of its freshness window (CachingRule.RefreshAfter). Obfuscated like
  // ourGzippedContentSuffix so it can never collide with a mangled artifact path.
  private static readonly string ourStoredDateSuffix = "-stamp-Kx2QwRm9Xb4tP";

  /// <summary>
  /// The stamp file recording when <paramref name="cacheFilePath"/> was stored. The date cannot live
  /// on the cache file itself: its LastWriteTime is deliberately the upstream's <c>Last-Modified</c>
  /// (so the served header and the ETag stay stable), and its creation time is unusable on Linux,
  /// where .NET reports <c>CreationTimeUtc</c> as <c>min(btime, mtime)</c> and cannot write btime at
  /// all — a forward touch is silently dropped there.
  /// </summary>
  public static string GetStoredDateStampPath(string cacheFilePath) => cacheFilePath + ourStoredDateSuffix;

  /// <summary>Whether <paramref name="path"/> is a stamp written by <see cref="GetStoredDateStampPath"/>.</summary>
  public static bool IsStoredDateStamp(string path) => path.EndsWith(ourStoredDateSuffix, StringComparison.Ordinal);

  /// <summary>The cache file a stamp belongs to. Only valid when <see cref="IsStoredDateStamp"/> holds.</summary>
  public static string GetStampedCacheFilePath(string stampPath) => stampPath[..^ourStoredDateSuffix.Length];

  extension(Uri uri)
  {
    public string GetFutureCacheFileLocation(string? contentEncoding = null) =>
      uri.ManglePath()
      + Path.GetExtension(uri.AbsolutePath)
      + contentEncoding switch
      {
        "gzip" => ourGzippedContentSuffix,
        "" or null => null,
        _ => throw new ArgumentException("Invalid content encoding", nameof(contentEncoding)),
      };

    public string ManglePath()
    {
      var path = uri.GetHostPortPath();
      var maxBytes = Encoding.UTF8.GetMaxByteCount(path.Length);

      byte[]? rented = null;
      var buffer = maxBytes <= 512 ? stackalloc byte[512] : rented = ArrayPool<byte>.Shared.Rent(maxBytes);

      try
      {
        var written = Encoding.UTF8.GetBytes(path, buffer);
        // Normalize the path delimiter to '/' so the same logical path hashes identically on every
        // platform. Do NOT case-fold: upstreams like Maven Central and npm are case-sensitive, so
        // e.g. 'Foo.jar' and 'foo.jar' are distinct artifacts and must map to distinct cache files.
        if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar)
        {
          for (var i = 0; i < written; i++)
          {
            if (buffer[i] == (byte)Path.DirectorySeparatorChar)
              buffer[i] = (byte)Path.AltDirectorySeparatorChar;
          }
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(buffer[..written]));
        return $"{hash[..2]}/{hash[2..4]}/{hash}";
      }
      finally
      {
        if (rented != null)
          ArrayPool<byte>.Shared.Return(rented);
      }
    }

    /// <summary>
    /// The scheme-agnostic <c>host[:port]/path</c> form of the URI (escaped). Used both to key cache
    /// files and to match upstreams against <see cref="UpstreamAuth.UrlPrefixes"/>, so every site must
    /// derive it identically — hence this single source.
    /// </summary>
    public string GetHostPortPath() =>
      uri.GetComponents(UriComponents.Host | UriComponents.Port | UriComponents.Path, UriFormat.UriEscaped);
  }
}
