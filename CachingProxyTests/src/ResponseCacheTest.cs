using System;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.CysharpMemoryPack;

namespace JetBrains.CachingProxy.Tests;

public class ResponseCacheTest
{
  // FusionCache has no TimeProvider/clock hook: it computes the L1 entry's AbsoluteExpiration
  // from the real system clock (DateTimeOffset.UtcNow). The FakeTimeProvider only drives the
  // MemoryCache's CheckExpired comparison (via TimeProviderClock). For the two clocks to be
  // coherent, the fake one must start at the real "now" -- otherwise (default epoch is the year
  // 2000) advancing it by minutes never reaches the real, ~year-2026 expiration and nothing ever
  // evicts. Starting at DateTimeOffset.UtcNow puts both clocks in the same era; the only real
  // time that elapses during a test is its own runtime (sub-second), negligible vs the minute-
  // scale durations under test, so the fake Advance() calls do all the work deterministically.
  private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.UtcNow);
  private readonly ResponseCache _cache;
  private readonly CacheDuration _cacheDuration = new()
  {
    [HttpStatusCode.TemporaryRedirect] = TimeSpan.FromMinutes(5)
  };

  public ResponseCacheTest()
  {
    var memoryCache = new MemoryCache(new MemoryCacheOptions
    {
      Clock = new TimeProviderClock(_timeProvider)
    });
    var fusionCache = new FusionCache(new FusionCacheOptions(), memoryCache);
    _cache = new ResponseCache(fusionCache, _timeProvider, new CachingProxyConfig());
  }

  [Fact]
  public async Task CacheEntry_ExpiresAfterAbsoluteTime()
  {
    const string key = "test-key";
    await _cache.PutStatusCode(key, HttpStatusCode.OK, _cacheDuration);

    // Entry should exist immediately
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Advance time past 5-minute expiration
    _timeProvider.Advance(TimeSpan.FromMinutes(6));

    // Entry should be expired
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CacheEntry_AccessDoesNotExtendExpiration()
  {
    const string key = "test-key";
    await _cache.PutStatusCode(key, HttpStatusCode.OK, _cacheDuration);

    // Simulate a high-load access pattern: access every 30 seconds.
    // With sliding expiration, these repeated accesses would keep extending the entry
    for (int i = 0; i < 9; i++) // 4.5 minutes of accesses (well within 5 min TTL)
    {
      _timeProvider.Advance(TimeSpan.FromSeconds(30));
      Assert.NotNull(await _cache.GetCachedStatusCode(key));
    }

    // Advance past the 5-minute expiration (total: 4.5 + 1.5 = 6 minutes)
    _timeProvider.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(30));

    // Entry should be expired despite repeated access
    // (with sliding expiration, it would still exist due to repeated access resetting the timer)
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Theory]
  [InlineData(HttpStatusCode.OK)]
  [InlineData(HttpStatusCode.NotFound)]
  public async Task CacheEntry_OkAndNotFound_ExpireAfter5Minutes(HttpStatusCode statusCode)
  {
    const string key = "test-key";
    await _cache.PutStatusCode(key, statusCode, _cacheDuration);

    // Should exist at 4:59
    _timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should be gone at 5:01
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CacheEntry_Redirect_ExpiresAfter5Minutes()
  {
    // The S3 "object is in the bucket" redirect must use the positive (5 min) duration, not the
    // 1-minute DefaultDuration, otherwise the redirect is re-probed/re-signed every minute.
    const string key = "test-key";
    await _cache.PutStatusCode(key, HttpStatusCode.RedirectKeepVerb, _cacheDuration);

    _timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Theory]
  [InlineData(HttpStatusCode.InternalServerError)]
  [InlineData(HttpStatusCode.ServiceUnavailable)]
  [InlineData(HttpStatusCode.GatewayTimeout)]
  public async Task CacheEntry_OtherStatusCodes_ExpireAfter1Minute(HttpStatusCode statusCode)
  {
    const string key = "test-key";
    await _cache.PutStatusCode(key, statusCode, _cacheDuration);

    // Should exist at 0:59
    _timeProvider.Advance(TimeSpan.FromSeconds(59));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should be gone at 1:01
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CustomCacheDuration_OverridesDefaultForOk()
  {
    const string key = "test-key";
    var customDuration = new CacheDuration(_cacheDuration)
    {
      [HttpStatusCode.OK] = TimeSpan.FromMinutes(30),
    };
    await _cache.PutStatusCode(key, HttpStatusCode.OK, customDuration);

    // Default OK is 5 minutes - with custom duration, should still exist at 10 minutes
    _timeProvider.Advance(TimeSpan.FromMinutes(10));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should exist just before 30-minute mark
    _timeProvider.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should be expired just after 30-minute mark
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CustomCacheDuration_ShortensExpirationForOk()
  {
    const string key = "test-key";
    var customDuration = _cacheDuration.Union(new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromSeconds(10),
    });
    await _cache.PutStatusCode(key, HttpStatusCode.OK, customDuration);

    // Should exist just before 10-second mark
    _timeProvider.Advance(TimeSpan.FromSeconds(9));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should be expired just after 10-second mark (well before the default 5 minutes)
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CustomCacheDuration_OverridesDefaultForNotFound()
  {
    const string key = "test-key";
    var customDuration = _cacheDuration.Union(new CacheDuration
    {
      [HttpStatusCode.NotFound] = TimeSpan.FromMinutes(1),
    });
    await _cache.PutStatusCode(key, HttpStatusCode.NotFound, customDuration);

    // Should exist just before 1-minute mark
    _timeProvider.Advance(TimeSpan.FromSeconds(59));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should be expired just after 1-minute mark (default NotFound is 5 minutes)
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CustomCacheDuration_AppliesToCustomStatusCode()
  {
    const string key = "test-key";
    var customDuration = _cacheDuration.Union(new CacheDuration
    {
      [HttpStatusCode.InternalServerError] = TimeSpan.FromMinutes(10),
    });
    await _cache.PutStatusCode(key, HttpStatusCode.InternalServerError, customDuration);

    // Default 500 falls back to DefaultDuration of 1 minute; custom is 10 minutes
    _timeProvider.Advance(TimeSpan.FromMinutes(5));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should exist just before 10-minute mark
    _timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should be expired just after 10-minute mark
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CustomCacheDuration_FallsBackToDefaultsForUnspecifiedStatusCode()
  {
    const string key = "test-key";
    // Custom duration only defines OK; NotFound should fall back to CacheDuration.Default (5 minutes)
    var customDuration = _cacheDuration.Union(new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromMinutes(30),
    });
    await _cache.PutStatusCode(key, HttpStatusCode.NotFound, customDuration);

    // Should exist just before the 5-minute default for NotFound
    _timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should be expired just after
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CustomCacheDuration_FallsBackToDefaultDurationForUnknownStatusCode()
  {
    const string key = "test-key";
    // Custom duration defines only OK; 500 is not in custom or in CacheDuration.Default,
    // so falls back to CacheDuration.DefaultDuration (1 minute)
    var customDuration = _cacheDuration.Union(new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromMinutes(30),
    });
    await _cache.PutStatusCode(key, HttpStatusCode.InternalServerError, customDuration);

    // Should exist just before 1-minute mark
    _timeProvider.Advance(TimeSpan.FromSeconds(59));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should be expired just after
    _timeProvider.Advance(TimeSpan.FromSeconds(2));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Theory]
  [InlineData(HttpStatusCode.OK)]
  [InlineData(HttpStatusCode.NotFound)]
  [InlineData(HttpStatusCode.InternalServerError)]
  public async Task CustomCacheDuration_AppliedFromCachingProxyPrefix(HttpStatusCode statusCode)
  {
    const string key = "test-key";
    // Simulate prefix-level configuration: a CachingProxyPrefix carries a CacheDuration
    // which is then forwarded to the cache layer.
    var prefix = new CachingProxyPrefix("repo.example.com", _cacheDuration.Union(new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.FromHours(1),
      [HttpStatusCode.NotFound] = TimeSpan.FromHours(1),
      [HttpStatusCode.InternalServerError] = TimeSpan.FromHours(1),
    }));
    await _cache.PutStatusCode(key, statusCode, prefix.CacheDuration!);

    // All three should remain past the defaults (5 min / 1 min) thanks to the 1-hour override
    _timeProvider.Advance(TimeSpan.FromMinutes(30));
    Assert.NotNull(await _cache.GetCachedStatusCode(key));

    // Should be expired just after the 1-hour mark
    _timeProvider.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1));
    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Theory]
  [InlineData(HttpStatusCode.Unauthorized)]
  [InlineData(HttpStatusCode.PaymentRequired)]
  [InlineData(HttpStatusCode.Forbidden)]
  public async Task CacheEntry_ZeroDurationStatusCodes_AreNotCached(HttpStatusCode statusCode)
  {
    // Auth / access errors have a zero cache duration: they must never be stored, so every request
    // re-probes upstream. PutStatusCode returns immediately without writing to the cache.
    const string key = "test-key";
    await _cache.PutStatusCode(key, statusCode, _cacheDuration);

    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CacheEntry_ZeroDuration_EntryReturnedWithoutBookkeepingHeaders()
  {
    // A non-cached entry is returned verbatim, without the proxy bookkeeping headers that only make
    // sense for a stored entry (there is no cache expiration to report).
    const string key = "test-key";
    var entry = await _cache.PutStatusCode(key, HttpStatusCode.Forbidden, _cacheDuration);

    Assert.False(entry.Headers.ContainsKey(CachingProxyConstants.CachedStatusHeader));
    Assert.False(entry.Headers.ContainsKey(CachingProxyConstants.CachedUntilHeader));
  }

  [Fact]
  public async Task CustomCacheDuration_ZeroDurationOverride_DisablesCachingForOk()
  {
    // Caching is driven purely by the duration, not by a hardcoded status list: a custom zero
    // duration for an otherwise-cacheable 200 disables caching for it too.
    const string key = "test-key";
    var customDuration = _cacheDuration.Union(new CacheDuration
    {
      [HttpStatusCode.OK] = TimeSpan.Zero,
    });
    await _cache.PutStatusCode(key, HttpStatusCode.OK, customDuration);

    Assert.Null(await _cache.GetCachedStatusCode(key));
  }

  [Fact]
  public async Task CachedUntilHeader_UsesDistributedDuration_WhenL2IsWired()
  {
    // When a distributed (L2) cache is configured, the durable lifetime is the (longer) L2 TTL:
    // the entry survives L1 eviction and keeps being served from L2 until then. The Cached-Until
    // header must report that durable expiration, not the shorter L1 duration -- otherwise it goes
    // stale (a timestamp in the past) once the in-memory copy is evicted but the entry is re-served.
    var memoryCache = new MemoryCache(new MemoryCacheOptions
    {
      Clock = new TimeProviderClock(_timeProvider)
    });
    CachedResponseFormatter.Register();
    var fusionCache = new FusionCache(new FusionCacheOptions(), memoryCache);
    fusionCache.SetupDistributedCache(
      new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
      new FusionCacheCysharpMemoryPackSerializer());

    var l1Duration = new CacheDuration { [HttpStatusCode.OK] = TimeSpan.FromMinutes(5) };
    var config = new CachingProxyConfig
    {
      DistributedCacheDuration = new CacheDuration { [HttpStatusCode.OK] = TimeSpan.FromMinutes(10) }
    };
    var cache = new ResponseCache(fusionCache, _timeProvider, config);

    var entry = await cache.PutStatusCode("test-key", HttpStatusCode.OK, l1Duration);

    var expected = (_timeProvider.GetUtcNow() + TimeSpan.FromMinutes(10)).ToString("R");
    Assert.Equal(expected, entry.Headers[CachingProxyConstants.CachedUntilHeader].ToString());
  }

  [Fact]
  public void GetCachingHeader_Anonymous_IsPublic()
  {
    // No identity established: the response is shared, so it stays publicly cacheable.
    var context = new DefaultHttpContext();

    Assert.Equal("public, max-age=31536000", CachedResponse.GetCachingHeader(context).ToString());
  }

  [Fact]
  public void GetCachingHeader_Authenticated_IsPrivate()
  {
    // A non-null AuthenticationType makes Identity.IsAuthenticated true: an authenticated response is
    // served only to the requesting client, so it must not be stored by shared/intermediary caches.
    var context = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "TestAuth"))
    };

    Assert.Equal("max-age=31536000, private", CachedResponse.GetCachingHeader(context).ToString());
  }
}
