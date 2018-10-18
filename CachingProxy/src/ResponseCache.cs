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
      Entry entry = new Entry(statusCode, DateTime.UtcNow + GetCacheTimeSpan(statusCode));
      myCache[cacheKey] = entry;
      return entry;
    }

    private static TimeSpan GetCacheTimeSpan(HttpStatusCode statusCode)
    {
      switch (statusCode)
      {
        // Clear reply from server
        case HttpStatusCode.OK:
        case HttpStatusCode.NotFound:
          return TimeSpan.FromMinutes(5);

        // Internal errors, network timeouts etc
        default: return TimeSpan.FromMinutes(1);
      }
    }

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
