using System;
using System.Collections.Concurrent;
using System.Net;

namespace JetBrains.CachingProxy
{
  // TODO. Cache cleanup after a some time?
  public class ResponseCache
  {
    public Entry? GetCachedStatusCode(string cacheKey)
    {
      if (!myCache.TryGetValue(cacheKey, out var entry)) return null;
      if (DateTime.UtcNow > entry.CacheUntil)
      {
        myCache.TryRemove(cacheKey, out _);
        return null;
      }

      return entry;
    }

    public Entry PutStatusCode(string cacheKey, HttpStatusCode statusCode, DateTimeOffset? lastModified, string? contentType, string? contentEncoding, long? contentLength)
    {
      var entry = new Entry(
        statusCode, DateTime.UtcNow + GetCacheTimeSpan(statusCode),
        lastModified: lastModified, contentType: contentType, contentEncoding: contentEncoding, contentLength: contentLength);
      myCache[cacheKey] = entry;
      return entry;
    }

    private static TimeSpan GetCacheTimeSpan(HttpStatusCode statusCode)
    {
      return statusCode switch
      {
        // Clear reply from a server
        HttpStatusCode.OK or HttpStatusCode.NotFound => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(1)
      };
    }

    private readonly ConcurrentDictionary<string, Entry> myCache = new(StringComparer.Ordinal);

    public class Entry
    {
      internal Entry(HttpStatusCode statusCode, DateTime cacheUntil, DateTimeOffset? lastModified, string? contentType, string? contentEncoding, long? contentLength)
      {
        CacheUntil = cacheUntil;
        LastModified = lastModified;
        ContentType = contentType;
        ContentLength = contentLength;
        ContentEncoding = contentEncoding;
        StatusCode = statusCode;
      }

      public readonly DateTime CacheUntil;
      public readonly HttpStatusCode StatusCode;
      public readonly DateTimeOffset? LastModified;
      public readonly string? ContentType;
      public readonly string? ContentEncoding;
      public readonly long? ContentLength;
    }
  }
}
