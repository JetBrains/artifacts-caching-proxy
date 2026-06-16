using System;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace JetBrains.CachingProxy;

/// <summary>
/// A cached response head: the status code to replay and the response headers to copy back
/// (representation headers such as Content-Type/Content-Length plus the proxy bookkeeping headers).
/// </summary>
public sealed record CachedResponse(HttpStatusCode StatusCode, IHeaderDictionary Headers);

public class ResponseCache(IMemoryCache cache, TimeProvider timeProvider)
{
  public CachedResponse? GetCachedStatusCode(string cacheKey) =>
    cache.TryGetValue<CachedResponse>(cacheKey, out var entry) ? entry : null;

  public CachedResponse PutStatusCode(string cacheKey, HttpStatusCode statusCode, CacheDuration cacheDuration) =>
    PutStatusCode(cacheKey, new CachedResponse(statusCode, new HeaderDictionary()), cacheDuration);

  public CachedResponse PutStatusCode(string cacheKey, CachedResponse entry, CacheDuration cacheDuration)
  {
    var cachingTime = cacheDuration.GetDuration(entry.StatusCode);
    entry.Headers[CachingProxyConstants.CachedStatusHeader] = entry.StatusCode.ToString("D");
    entry.Headers[CachingProxyConstants.CachedUntilHeader] = (timeProvider.GetUtcNow() + cachingTime).ToString("R");

    return cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
    {
      AbsoluteExpirationRelativeToNow = cachingTime,
    });
  }
}
