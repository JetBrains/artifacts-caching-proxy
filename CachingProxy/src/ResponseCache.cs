using System;
using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace JetBrains.CachingProxy;

public class ResponseCache(IMemoryCache cache)
{
  public Entry? GetCachedStatusCode(string cacheKey) =>
    cache.TryGetValue<Entry>(cacheKey, out var entry) ? entry : null;

  public Entry PutStatusCode(string cacheKey, HttpStatusCode statusCode, CacheDuration? cacheDuration, DateTimeOffset? lastModified = null,
    string? contentType = null, string? contentEncoding = null, long? contentLength = null)
  {
    var entry = new Entry(statusCode, cacheDuration, lastModified, contentType, contentEncoding, contentLength);
    return cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
    {
      AbsoluteExpirationRelativeToNow = entry.GetCacheTimeSpan(),
    });
  }

  public record Entry(
    HttpStatusCode StatusCode,
    CacheDuration? CacheDuration,
    DateTimeOffset? LastModified,
    string? ContentType,
    string? ContentEncoding,
    long? ContentLength)
  {
    public TimeSpan GetCacheTimeSpan() =>
      CacheDuration?.TryGetValue(StatusCode, out var timeSpan) ?? false ? timeSpan :
        CacheDuration.Default.TryGetValue(StatusCode, out timeSpan) ? timeSpan : CacheDuration.DefaultDuration;
  }
}
