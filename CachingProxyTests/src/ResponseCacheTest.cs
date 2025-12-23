using System;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class ResponseCacheTest
{
  // Adapter to bridge FakeTimeProvider to ISystemClock (used by MemoryCache)
  private class FakeSystemClock(FakeTimeProvider timeProvider) : ISystemClock
  {
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
  }

  private readonly FakeTimeProvider _timeProvider = new();
  private readonly ResponseCache _cache;

  public ResponseCacheTest()
  {
    var memoryCache = new MemoryCache(new MemoryCacheOptions
    {
      Clock = new FakeSystemClock(_timeProvider)
    });
    _cache = new ResponseCache(memoryCache);
  }

  [Fact]
  public void CacheEntry_ExpiresAfterAbsoluteTime()
  {
    const string key = "test-key";
    _cache.PutStatusCode(key, HttpStatusCode.OK, null, null, null, null);

    // Entry should exist immediately
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Advance time past 5-minute expiration
    _timeProvider.Advance(TimeSpan.FromMinutes(6));

    // Entry should be expired
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Fact]
  public void CacheEntry_AccessDoesNotExtendExpiration()
  {
    const string key = "test-key";
    _cache.PutStatusCode(key, HttpStatusCode.OK, null, null, null, null);

    // Simulate high-load access pattern: access every 30 seconds
    // With sliding expiration, these repeated accesses would keep extending the entry
    for (int i = 0; i < 9; i++) // 4.5 minutes of accesses (well within 5 min TTL)
    {
      _timeProvider.Advance(TimeSpan.FromSeconds(30));
      Assert.NotNull(_cache.GetCachedStatusCode(key));
    }

    // Advance past the 5-minute expiration (total: 4.5 + 1.5 = 6 minutes)
    _timeProvider.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(30));

    // Entry should be expired despite repeated access
    // (with sliding expiration, it would still exist due to repeated access resetting the timer)
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Theory]
  [InlineData(HttpStatusCode.OK)]
  [InlineData(HttpStatusCode.NotFound)]
  public void CacheEntry_OkAndNotFound_ExpireAfter5Minutes(HttpStatusCode statusCode)
  {
    const string key = "test-key";
    _cache.PutStatusCode(key, statusCode, null, null, null, null);

    // Should exist at 4:59
    _timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be gone at 5:01
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Theory]
  [InlineData(HttpStatusCode.InternalServerError)]
  [InlineData(HttpStatusCode.ServiceUnavailable)]
  [InlineData(HttpStatusCode.GatewayTimeout)]
  public void CacheEntry_OtherStatusCodes_ExpireAfter1Minute(HttpStatusCode statusCode)
  {
    const string key = "test-key";
    _cache.PutStatusCode(key, statusCode, null, null, null, null);

    // Should exist at 0:59
    _timeProvider.Advance(TimeSpan.FromSeconds(59));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be gone at 1:01
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }
}
