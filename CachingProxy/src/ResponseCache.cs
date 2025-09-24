using System;
using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace JetBrains.CachingProxy;

public class ResponseCache(IMemoryCache cache)
{
  public Entry? GetCachedStatusCode(string cacheKey) =>
    cache.TryGetValue<Entry>(cacheKey, out var entry) ? entry : null;

  public Entry PutStatusCode(string cacheKey, HttpStatusCode statusCode, DateTimeOffset? lastModified,
    string? contentType, string? contentEncoding, long? contentLength)
  {
    var entry = new Entry(statusCode, lastModified, contentType, contentEncoding, contentLength);
    return cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
    {
      SlidingExpiration = entry.GetCacheTimeSpan(),
    });
  }

  public record Entry(
    HttpStatusCode StatusCode,
    DateTimeOffset? LastModified,
    string? ContentType,
    string? ContentEncoding,
    long? ContentLength)
  {
    public TimeSpan GetCacheTimeSpan() => StatusCode switch
    {
      // Clear reply from a server
      HttpStatusCode.OK or HttpStatusCode.NotFound => TimeSpan.FromMinutes(5),
      _ => TimeSpan.FromMinutes(1)
    };

    public DateTimeOffset CacheUntil => DateTimeOffset.Now + GetCacheTimeSpan();
  }
}
