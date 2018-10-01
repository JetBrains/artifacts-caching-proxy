using System;
using System.Collections.Concurrent;
using System.Net;
using JetBrains.Annotations;

namespace JetBrains.CachingProxy
{
  // TODO. Cache cleanup after a some time?
  public class ResponseCache
  {
    [CanBeNull]
    public Entry GetCachedStatusCode(string cacheKey)
    {
      if (!myCache.TryGetValue(cacheKey, out var entry)) return null;
      return DateTime.UtcNow > entry.CacheUntil ? null : entry;
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
