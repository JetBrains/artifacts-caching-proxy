using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy
{
  public class CacheFileProvider : IFileProvider
  {
    private static readonly string ourGzippedContentSuffix = "-gzip-Ege4dHyCEA7IM";

    private readonly RemoteServers myRemoteServers;
    private readonly PhysicalFileProvider myPhysicalFileProvider;

    public CacheFileProvider(CachingProxyConfig config, RemoteServers? remoteServers = null)
    {
      myRemoteServers = remoteServers ?? new RemoteServers(config);
      var localCachePath = config.LocalCachePath;
      if (string.IsNullOrEmpty(localCachePath))
        throw new ArgumentNullException(nameof(localCachePath), "LocalCachePath could not be null");
      if (!Directory.Exists(localCachePath))
      {
        if (localCachePath.StartsWith(Path.GetTempPath()))
          Directory.CreateDirectory(localCachePath);
        else
          throw new ArgumentException("LocalCachePath doesn't exist: " + localCachePath);
      }

      myPhysicalFileProvider = new PhysicalFileProvider(localCachePath);
    }

    public IFileInfo GetFileInfo(string subpath)
    {
      if (myRemoteServers.LookupRemoteServer(subpath, out var remainingPart) is not {} remoteServer)
        return new NotFoundFileInfo(subpath);

      // Mangle once: the gzip and plain variants differ only by a suffix appended after the
      // (identical) hash, so ManglePath(subpath, suffix) == ManglePath(subpath) + suffix.
      var mangled = ManglePath(remoteServer, remainingPart);
      var withGzipSuffix = myPhysicalFileProvider.GetFileInfo(mangled + ourGzippedContentSuffix);
      return withGzipSuffix.Exists ? withGzipSuffix : myPhysicalFileProvider.GetFileInfo(mangled);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => throw new NotSupportedException();

    public string? GetFutureCacheFileLocation(RemoteServers.RemoteServer remoteServer, PathString remainingPart, string? contentEncoding) =>
      myPhysicalFileProvider.GetFileInfo(ManglePath(remoteServer, remainingPart, GetContentEncodingCacheFileSuffix(contentEncoding))).PhysicalPath;

    public string? GetContentEncoding(IFileInfo fileInfo) =>
      fileInfo.PhysicalPath?.EndsWith(ourGzippedContentSuffix) ?? false ? "gzip" : null;

    // Sidecar file holding the upstream Content-Type next to a cached artifact. Persisting it lets a
    // HIT serve the original type verbatim instead of guessing from the file extension.
    private const string ourContentTypeSidecarSuffix = ".content-type";

    public string GetContentTypeSidecarPath(string cacheFilePath) => cacheFilePath + ourContentTypeSidecarSuffix;

    public string? GetStoredContentType(IFileInfo fileInfo)
    {
      var physicalPath = fileInfo.PhysicalPath;
      if (physicalPath == null)
        return null;
      try
      {
        var sidecar = physicalPath + ourContentTypeSidecarSuffix;
        return File.Exists(sidecar) ? File.ReadAllText(sidecar) : null;
      }
      catch (IOException)
      {
        return null;
      }
    }

    public IChangeToken Watch(string filter) => throw new NotSupportedException();

    private static string ManglePath(RemoteServers.RemoteServer remoteServer, PathString remainingPart, string? contentEncodingSuffix = null)
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

    private static string GetContentEncodingCacheFileSuffix(string? contentEncoding) =>
      contentEncoding switch
      {
        null => "",
        "gzip" => ourGzippedContentSuffix,
        _ => throw new ArgumentException($"Unsupported Content-Encoding: {contentEncoding}")
      };
  }
}
