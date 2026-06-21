using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ZiggyCreatures.Caching.Fusion;

namespace JetBrains.CachingProxy;

/// <summary>
/// A cached response head: the status code to replay and the response headers to copy back
/// (representation headers such as Content-Type/Content-Length plus the proxy bookkeeping headers).
/// </summary>
public sealed record CachedResponse(HttpStatusCode StatusCode, IHeaderDictionary Headers, byte[]? Body = null)
{
  public CachedResponse(HttpResponseMessage response) : this(response.StatusCode, new HeaderDictionary())
  {
    Headers.LastModified = response.Content.Headers.LastModified?.ToString("R");
    Headers.ContentLength = response.Content.Headers.ContentLength;
    Headers.ContentType = response.Content.Headers.ContentType?.ToString();
    Headers.ContentEncoding = new StringValues([..response.Content.Headers.ContentEncoding]);
    Headers.ETag = response.Headers.ETag?.ToString();
  }

  public CachedResponse(GetObjectResponse response) : this(response.HttpStatusCode, new HeaderDictionary())
  {
    Headers.LastModified = response.LastModified?.ToString("R");
    Headers.ContentLength = response.ContentLength;
    Headers.ContentType = response.Headers.ContentType;
    Headers.ContentEncoding = response.Headers.ContentEncoding;
    Headers.ETag = response.ETag;
  }

  public async ValueTask InvokeAsync(HttpContext context)
  {
    foreach (var (key, value) in Headers)
    {
      context.Response.Headers[key] = value;
    }
    if (StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
    {
      // For successful (2xx) responses, the cached response is always eternally cacheable.
      context.Response.Headers.CacheControl = EternalCachingHeader;
    }

    context.Response.StatusCode = (int)StatusCode;
    if (Body != null)
      await context.Response.BodyWriter.WriteAsync(Body, context.RequestAborted);
  }

  public static readonly StringValues EternalCachingHeader =
    new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365) }.ToString();

  public static readonly CachedResponse MethodNotAllowed = new(HttpStatusCode.MethodNotAllowed, new HeaderDictionary());
  public static readonly CachedResponse InvalidPath = new(HttpStatusCode.BadRequest, new HeaderDictionary(), [.. "Invalid request path"u8]);
  public static readonly CachedResponse Blacklisted = new(HttpStatusCode.NotFound, new HeaderDictionary(), [.. "Blacklisted"u8]);
}

public class ResponseCache(IFusionCache cache, TimeProvider timeProvider, CachingProxyConfig config)
{
  public ValueTask<CachedResponse?> GetCachedStatusCode(string cacheKey, CancellationToken cancellationToken = default) =>
    cache.GetOrDefaultAsync<CachedResponse>(cacheKey, token: cancellationToken);

  public ValueTask<CachedResponse> PutStatusCode(string cacheKey, HttpStatusCode statusCode, CacheDuration cacheDuration, CancellationToken cancellationToken = default) =>
    PutStatusCode(cacheKey, new CachedResponse(statusCode, new HeaderDictionary()), cacheDuration, cancellationToken);

  public async ValueTask<CachedResponse> PutStatusCode(string cacheKey, CachedResponse entry, CacheDuration cacheDuration, CancellationToken cancellationToken = default)
  {
    var cachingTime = cacheDuration.GetDuration(entry.StatusCode);
    // L2 (distributed/Redis) TTL is controlled by the global DistributedCacheDuration, but it is never
    // allowed to be shorter than the L1 TTL: the durable backing store must outlive the in-process copy.
    var l2CachingTime = config.DistributedCacheDuration.GetDuration(entry.StatusCode);
    var distributedCachingTime = l2CachingTime > cachingTime ? l2CachingTime : cachingTime;
    // The durable lifetime is the L2 TTL when a distributed cache is wired (the entry survives L1
    // eviction and is re-served from L2 until then); otherwise it is just the L1 TTL. Reporting the
    // durable expiration keeps the header in the future for as long as the entry is actually cached,
    // instead of going stale once the in-memory copy is evicted.
    var durableCachingTime = cache.HasDistributedCache ? distributedCachingTime : cachingTime;
    entry.Headers[CachingProxyConstants.CachedStatusHeader] = entry.StatusCode.ToString("D");
    entry.Headers[CachingProxyConstants.CachedUntilHeader] = (timeProvider.GetUtcNow() + durableCachingTime).ToString("R");

    await cache.SetAsync(cacheKey, entry, new FusionCacheEntryOptions
    {
      MemoryCacheDuration = cachingTime,
      DistributedCacheDuration = distributedCachingTime,
    }, token: cancellationToken);
    return entry;
  }
}
