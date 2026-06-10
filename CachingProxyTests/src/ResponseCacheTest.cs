using System;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class ResponseCacheTest
{
  private readonly FakeTimeProvider _timeProvider = new();
  private readonly ResponseCache _cache;

  public ResponseCacheTest()
  {
    var memoryCache = new MemoryCache(new MemoryCacheOptions
    {
      Clock = new TimeProviderClock(_timeProvider)
    });
    _cache = new ResponseCache(memoryCache, _timeProvider);
  }

  [Fact]
  public void CacheEntry_ExpiresAfterAbsoluteTime()
  {
    const string key = "test-key";
    _cache.PutStatusCode(key, HttpStatusCode.OK);

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
    _cache.PutStatusCode(key, HttpStatusCode.OK);

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
    _cache.PutStatusCode(key, statusCode);

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
    _cache.PutStatusCode(key, statusCode);

    // Should exist at 0:59
    _timeProvider.Advance(TimeSpan.FromSeconds(59));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be gone at 1:01
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Fact]
  public void CustomCacheDuration_OverridesDefaultForOk()
  {
    const string key = "test-key";
    var customDuration = new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromMinutes(30),
    };
    _cache.PutStatusCode(key, HttpStatusCode.OK, customDuration);

    // Default OK is 5 minutes - with custom duration, should still exist at 10 minutes
    _timeProvider.Advance(TimeSpan.FromMinutes(10));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should exist just before 30-minute mark
    _timeProvider.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be expired just after 30-minute mark
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Fact]
  public void CustomCacheDuration_ShortensExpirationForOk()
  {
    const string key = "test-key";
    var customDuration = new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromSeconds(10),
    };
    _cache.PutStatusCode(key, HttpStatusCode.OK, customDuration);

    // Should exist just before 10-second mark
    _timeProvider.Advance(TimeSpan.FromSeconds(9));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be expired just after 10-second mark (well before the default 5 minutes)
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Fact]
  public void CustomCacheDuration_OverridesDefaultForNotFound()
  {
    const string key = "test-key";
    var customDuration = new CacheDuration
    {
      [HttpStatusCode.NotFound] = TimeSpan.FromMinutes(1),
    };
    _cache.PutStatusCode(key, HttpStatusCode.NotFound, customDuration);

    // Should exist just before 1-minute mark
    _timeProvider.Advance(TimeSpan.FromSeconds(59));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be expired just after 1-minute mark (default NotFound is 5 minutes)
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Fact]
  public void CustomCacheDuration_AppliesToCustomStatusCode()
  {
    const string key = "test-key";
    var customDuration = new CacheDuration
    {
      [HttpStatusCode.InternalServerError] = TimeSpan.FromMinutes(10),
    };
    _cache.PutStatusCode(key, HttpStatusCode.InternalServerError, customDuration);

    // Default 500 falls back to DefaultDuration of 1 minute; custom is 10 minutes
    _timeProvider.Advance(TimeSpan.FromMinutes(5));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should exist just before 10-minute mark
    _timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be expired just after 10-minute mark
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Fact]
  public void CustomCacheDuration_FallsBackToDefaultsForUnspecifiedStatusCode()
  {
    const string key = "test-key";
    // Custom duration only defines OK; NotFound should fall back to CacheDuration.Default (5 minutes)
    var customDuration = new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromMinutes(30),
    };
    _cache.PutStatusCode(key, HttpStatusCode.NotFound, customDuration);

    // Should exist just before the 5-minute default for NotFound
    _timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be expired just after
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Fact]
  public void CustomCacheDuration_FallsBackToDefaultDurationForUnknownStatusCode()
  {
    const string key = "test-key";
    // Custom duration defines only OK; 500 is not in custom or in CacheDuration.Default,
    // so falls back to CacheDuration.DefaultDuration (1 minute)
    var customDuration = new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromMinutes(30),
    };
    _cache.PutStatusCode(key, HttpStatusCode.InternalServerError, customDuration);

    // Should exist just before 1-minute mark
    _timeProvider.Advance(TimeSpan.FromSeconds(59));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be expired just after
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }

  [Theory]
  [InlineData(HttpStatusCode.OK)]
  [InlineData(HttpStatusCode.NotFound)]
  [InlineData(HttpStatusCode.InternalServerError)]
  public void CustomCacheDuration_AppliedFromCachingProxyPrefix(HttpStatusCode statusCode)
  {
    const string key = "test-key";
    // Simulate prefix-level configuration: a CachingProxyPrefix carries a CacheDuration
    // which is then forwarded to the cache layer.
    var prefix = new CachingProxyPrefix("repo.example.com", new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromHours(1),
      [HttpStatusCode.NotFound] = TimeSpan.FromHours(1),
      [HttpStatusCode.InternalServerError] = TimeSpan.FromHours(1),
    });
    _cache.PutStatusCode(key, statusCode, prefix.CacheDuration);

    // All three should remain past the defaults (5 min / 1 min) thanks to the 1-hour override
    _timeProvider.Advance(TimeSpan.FromMinutes(30));
    Assert.NotNull(_cache.GetCachedStatusCode(key));

    // Should be expired just after the 1-hour mark
    _timeProvider.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1));
    Assert.Null(_cache.GetCachedStatusCode(key));
  }
}
