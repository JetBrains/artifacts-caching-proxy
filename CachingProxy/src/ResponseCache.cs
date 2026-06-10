using System;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;

namespace JetBrains.CachingProxy;

public class ResponseCache(IMemoryCache cache, TimeProvider timeProvider)
{
  public IHttpResponseFeature? GetCachedStatusCode(string cacheKey) =>
    cache.TryGetValue<IHttpResponseFeature>(cacheKey, out var entry) ? entry : null;

  public IHttpResponseFeature PutStatusCode(string cacheKey, HttpStatusCode statusCode, CacheDuration? cacheDuration = null) =>
    PutStatusCode(cacheKey, cacheDuration, new HttpResponseFeature { StatusCode = (int)statusCode });

  public IHttpResponseFeature PutStatusCode(string cacheKey, CacheDuration? cacheDuration, IHttpResponseFeature entry)
  {
    var cachingTime = GetCacheDuration(cacheDuration, (HttpStatusCode)entry.StatusCode);
    entry.Headers[CachingProxyConstants.CachedStatusHeader] = entry.StatusCode.ToString();
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
