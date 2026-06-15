using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy;

public static class CacheFileProvider
{
  private static readonly string ourGzippedContentSuffix = "-gzip-Ege4dHyCEA7IM";

  public static string GetFutureCacheFileLocation(RemoteServers.RemoteServer remoteServer, string? remainingPart, StringSegment? contentEncoding = null) =>
    ManglePath(remoteServer, remainingPart, contentEncoding?.ToString() switch
    {
      "gzip" => ourGzippedContentSuffix,
      var value  => value
    });

  private static string ManglePath(RemoteServers.RemoteServer remoteServer, string? remainingPart, string? contentEncodingSuffix = null)
  {
    var path = remoteServer.GetUpstreamUriKey(remainingPart);
    var maxBytes = Encoding.UTF8.GetMaxByteCount(path.Length);

    byte[]? rented = null;
    var buffer = maxBytes <= 512
      ? stackalloc byte[512]
      : rented = ArrayPool<byte>.Shared.Rent(maxBytes);

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
      return $"{hash[..2]}/{hash[2..4]}/{hash}{Path.GetExtension(path)}{contentEncodingSuffix}";
    }
    finally
    {
      if (rented != null)
        ArrayPool<byte>.Shared.Return(rented);
    }
  }
}
