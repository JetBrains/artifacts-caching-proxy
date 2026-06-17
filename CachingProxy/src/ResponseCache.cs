using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

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
