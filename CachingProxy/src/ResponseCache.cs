using System;
using System.Collections.Concurrent;
using System.Net;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Hosting;

namespace JetBrains.CachingProxy
{
  // TODO. Cache cleanup after a some time?
  public class ResponseCache
  {
    private readonly IHostingEnvironment myHostingEnvironment;

    public ResponseCache(IHostingEnvironment hostingEnvironment)
    {
      myHostingEnvironment = hostingEnvironment;
    }

    [CanBeNull]
    public Entry GetCachedStatusCode(string cacheKey)
    {
      if (!myCache.TryGetValue(cacheKey, out var entry)) return null;
      if (DateTime.UtcNow > entry.CacheUntil)
      {
        myCache.TryRemove(cacheKey, out _);
        return null;
      }

      return entry;
    }

    [NotNull]
    public Entry PutStatusCode(string cacheKey, HttpStatusCode statusCode)
    {
      Entry entry = new Entry(statusCode, DateTime.UtcNow + ourCacheTimeSpan);
      myCache[cacheKey] = entry;
      return entry;
    }

    private static readonly TimeSpan ourCacheTimeSpan = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> myCache =
      new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

    public class Entry
    {
      internal Entry(HttpStatusCode statusCode, DateTime cacheUntil)
      {
        CacheUntil = cacheUntil;
        StatusCode = statusCode;
      }

      public readonly DateTime CacheUntil;
      public readonly HttpStatusCode StatusCode;
    }
  }
}
