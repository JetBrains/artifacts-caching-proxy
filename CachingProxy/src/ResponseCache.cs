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
  /// <summary>
  /// Builds the cache key for a request. The HTTP method is part of the key because some cached
  /// responses are verb-specific — most importantly an S3 presigned redirect, which is signed for
  /// either GET or HEAD — so a HEAD and a GET for the same path must not share an entry.
  /// </summary>
  public static string CacheKey(string method, string path) =>
    (HttpMethods.IsHead(method) ? "HEAD " : "GET ") + path;

  public CachedResponse? GetCachedStatusCode(string cacheKey) =>
    cache.TryGetValue<CachedResponse>(cacheKey, out var entry) ? entry : null;

  public CachedResponse PutStatusCode(string cacheKey, HttpStatusCode statusCode, CacheDuration? cacheDuration = null) =>
    PutStatusCode(cacheKey, cacheDuration, new CachedResponse(statusCode, new HeaderDictionary()));

  public CachedResponse PutStatusCode(string cacheKey, CacheDuration? cacheDuration, CachedResponse entry)
  {
    var cachingTime = GetCacheDuration(cacheDuration, entry.StatusCode);
    entry.Headers[CachingProxyConstants.CachedStatusHeader] = entry.StatusCode.ToString("D");
    entry.Headers[CachingProxyConstants.CachedUntilHeader] = (timeProvider.GetUtcNow() + cachingTime).ToString("R");

    return cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
    {
      AbsoluteExpirationRelativeToNow = cachingTime,
    });
  }

  public static TimeSpan GetCacheDuration(CacheDuration? cacheDuration, HttpStatusCode statusCode) =>
    cacheDuration?.TryGetValue(statusCode, out var timeSpan) ?? false ? timeSpan :
    CacheDuration.Default.TryGetValue(statusCode, out timeSpan) ? timeSpan : CacheDuration.DefaultDuration;
}
