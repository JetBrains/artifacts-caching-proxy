using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace JetBrains.CachingProxy;

/// <summary>
/// What a stored cache file needs beside its bytes: when we stored it — the anchor of its freshness
/// window, see <see cref="CachingRule.RefreshAfter"/> — plus the parts of the upstream's response head
/// that the bytes alone cannot reproduce.
/// <para>All of it lives in one small companion text file per cache file (see
/// <see cref="CacheFileProvider.GetMetadataPath"/>), so a stored copy costs one write to record and one
/// read to replay however many values it carries. That set mirrors what the S3 backend reads back off
/// the object and its user metadata (see the <c>GetObjectResponse</c> constructor of
/// <see cref="CachedResponse"/>), so a HIT is served with the same head whichever backend holds it.</para>
/// </summary>
public sealed record CacheEntryMetadata(
  DateTime StoredAtUtc,
  string? ContentType = null,
  string? ETag = null,
  DateTimeOffset? LastModified = null,
  string? Digest = null)
{
  // Our own field, so it gets a name no HTTP header has.
  private const string StoredAtField = "StoredAt";

  /// <summary>The metadata to record for a body just fetched from the upstream.</summary>
  public static CacheEntryMetadata FromResponse(DateTime storedAtUtc, HttpResponseMessage response) => new(
    storedAtUtc,
    response.Content.Headers.ContentType?.ToString(),
    response.Headers.ETag?.ToString(),
    response.Content.Headers.LastModified,
    response.Headers.TryGetValues(CachedResponse.DockerContentDigestHeader, out var digest) ? digest.FirstOrDefault() : null);

  /// <summary>One <c>Name: value</c> per line; absent values are omitted.</summary>
  public string Format()
  {
    var text = new StringBuilder();
    Append(text, StoredAtField, StoredAtUtc.ToString("O", CultureInfo.InvariantCulture));
    Append(text, HeaderNames.ContentType, ContentType);
    Append(text, HeaderNames.ETag, ETag);
    Append(text, HeaderNames.LastModified, LastModified?.ToString("R", CultureInfo.InvariantCulture));
    Append(text, CachedResponse.DockerContentDigestHeader, Digest);
    return text.ToString();
  }

  // HTTP header parsing rejects a newline inside a value, so one cannot reach here from an upstream;
  // stripped anyway so a malformed value can only truncate itself rather than the rest of the file.
  private static void Append(StringBuilder text, string name, string? value)
  {
    if (string.IsNullOrEmpty(value)) return;
    text.Append(name).Append(": ").Append(value.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
  }

  /// <summary>
  /// Null when <paramref name="text"/> carries no stored date: an empty or truncated companion file, or
  /// one written in a shape this version does not recognise. The caller treats that as "no metadata",
  /// which costs one refetch and heals the entry. Unknown field names are ignored instead, so a file
  /// written by another version still parses as far as the two agree.
  /// </summary>
  public static CacheEntryMetadata? TryParse(string text)
  {
    DateTime? storedAt = null;
    string? contentType = null, etag = null, digest = null;
    DateTimeOffset? lastModified = null;

    foreach (var line in text.Split('\n'))
    {
      // The first ':' is the separator; every value that has one of its own (a date, a digest) keeps it.
      var colon = line.IndexOf(':');
      if (colon < 0) continue;

      var name = line[..colon].Trim();
      var value = line[(colon + 1)..].Trim();
      if (value.Length == 0) continue;

      if (Is(name, StoredAtField))
        storedAt = DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ?
          parsed.ToUniversalTime() : null;
      else if (Is(name, HeaderNames.ContentType)) contentType = value;
      else if (Is(name, HeaderNames.ETag)) etag = value;
      else if (Is(name, HeaderNames.LastModified))
        lastModified = DateTimeOffset.TryParseExact(value, "R", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ?
          parsed : null;
      else if (Is(name, CachedResponse.DockerContentDigestHeader)) digest = value;
    }

    return storedAt is { } when ? new CacheEntryMetadata(when, contentType, etag, lastModified, digest) : null;
  }

  private static bool Is(string name, string field) => name.Equals(field, StringComparison.OrdinalIgnoreCase);
}

public static class CacheFileProvider
{
  private static readonly string ourGzippedContentSuffix = "-gzip-Ege4dHyCEA7IM";

  // Suffix of the companion metadata file (see CacheEntryMetadata). Obfuscated like
  // ourGzippedContentSuffix so it can never collide with a mangled artifact path.
  private static readonly string ourMetadataSuffix = "-meta-Kx2QwRm9Xb4tP";

  /// <summary>
  /// The metadata file belonging to <paramref name="cacheFilePath"/>. A companion file rather than the
  /// cache file's own attributes because a filesystem has nowhere to put a media type, an ETag or a
  /// digest — and no usable slot for the stored date either: the file's LastWriteTime is deliberately
  /// the upstream's <c>Last-Modified</c>, and its creation time is unusable on Linux, where .NET
  /// reports <c>CreationTimeUtc</c> as <c>min(btime, mtime)</c> and cannot write btime at all, so a
  /// forward touch is silently dropped there.
  /// </summary>
  public static string GetMetadataPath(string cacheFilePath) => cacheFilePath + ourMetadataSuffix;

  /// <summary>Whether <paramref name="path"/> is a metadata file written for some cache file.</summary>
  public static bool IsMetadata(string path) => path.EndsWith(ourMetadataSuffix, StringComparison.Ordinal);

  /// <summary>The cache file metadata belongs to. Only valid when <see cref="IsMetadata"/> holds.</summary>
  public static string GetMetadataOwnerPath(string metadataPath) => metadataPath[..^ourMetadataSuffix.Length];

  extension(Uri uri)
  {
    public string GetFutureCacheFileLocation(string? contentEncoding = null, string? variant = null) =>
      uri.ManglePath(variant)
      + Path.GetExtension(uri.AbsolutePath)
      + contentEncoding switch
      {
        "gzip" => ourGzippedContentSuffix,
        "" or null => null,
        _ => throw new ArgumentException("Invalid content encoding", nameof(contentEncoding)),
      };

    /// <param name="variant">
    /// Extra cache-key dimension for a content-negotiated endpoint (see
    /// <see cref="RemoteProxy.GetCacheVariant"/>), or null for the usual path-only key. It is hashed
    /// after a newline, which an escaped URI path can never contain, so a variant-keyed entry can never
    /// collide with a plain one — including for the empty variant a client that sent no Accept yields.
    /// </param>
    public string ManglePath(string? variant = null)
    {
      var path = uri.GetHostPortPath();
      var maxBytes = Encoding.UTF8.GetMaxByteCount(path.Length)
                     + (variant != null ? 1 + Encoding.UTF8.GetMaxByteCount(variant.Length) : 0);

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
        // Appended after the separator normalization above: a media type contains '/', which is the
        // delimiter that loop rewrites *to*, so normalizing it would be a no-op at best.
        if (variant != null)
        {
          buffer[written++] = (byte)'\n';
          written += Encoding.UTF8.GetBytes(variant, buffer[written..]);
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
