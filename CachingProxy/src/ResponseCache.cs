using System;
using System.Collections.Generic;
using System.Globalization;
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
  /// <summary>
  /// The digest of the manifest or blob a registry returned. An OCI client resolves a tag to this value
  /// and containerd verifies the body against it, so a cache HIT that dropped the header would break a
  /// pull that a MISS served fine. It cannot be re-derived from the stored body (it covers the exact
  /// bytes of the specific representation), hence the pass-through here and the S3 metadata below.
  /// </summary>
  public const string DockerContentDigestHeader = "Docker-Content-Digest";

  /// <summary>S3 user-metadata key carrying <see cref="DockerContentDigestHeader"/> through the bucket.</summary>
  public const string DockerContentDigestMetadataKey = "docker-content-digest";

  /// <summary>
  /// S3 user-metadata key carrying the upstream's Last-Modified for the stored bytes (round-trippable "O"
  /// format), absent for an upstream that sends none. Written by <c>S3CachingMiddleware</c>, which also
  /// revalidates against it. Needed because the object's native LastModified is when S3 took the bytes,
  /// and the metadata-only freshness touch moves it again on every 304 - a date that drifts forward while
  /// the entity stands still.
  /// </summary>
  public const string UpstreamLastModifiedMetadataKey = "upstream-last-modified";

  /// <summary>
  /// The upstream's recorded date for a stored object's bytes, or null when none was recorded - an
  /// upstream that sends none, or an object stored before this was recorded.
  /// </summary>
  public static DateTimeOffset? UpstreamLastModified(GetObjectResponse response) =>
    DateTimeOffset.TryParse(response.Metadata[UpstreamLastModifiedMetadataKey], CultureInfo.InvariantCulture,
      DateTimeStyles.RoundtripKind, out var lastModified)
      ? lastModified
      : null;

  // Copied through verbatim. Besides the digest, Docker-Distribution-API-Version is how a client confirms
  // it is talking to a v2 registry.
  private static readonly string[] ourRegistryHeaders =
    [DockerContentDigestHeader, CachingProxyConstants.DockerApiVersionHeader];

  // Internal bookkeeping that travels with a cached entry but must never be written back to a client.
  private static readonly HashSet<string> ourExcludeHeaders =
    new(StringComparer.OrdinalIgnoreCase) { CachingProxyConstants.CachedContentLengthHeader };

  public CachedResponse(HttpResponseMessage response) : this(response.StatusCode, new HeaderDictionary())
  {
    Headers.LastModified = response.Content.Headers.LastModified?.ToString("R");
    Headers.ContentLength = response.Content.Headers.ContentLength;
    Headers.ContentType = response.Content.Headers.ContentType?.ToString();
    Headers.ContentEncoding = new StringValues([..response.Content.Headers.ContentEncoding]);
    Headers.ETag = response.Headers.ETag?.ToString();
    foreach (var name in ourRegistryHeaders)
    {
      if (response.Headers.TryGetValues(name, out var values))
        Headers[name] = new StringValues([..values]);
    }
  }

  public CachedResponse(GetObjectResponse response) : this(response.HttpStatusCode, new HeaderDictionary())
  {
    // The entity's own date when the bucket carries it, so a HIT describes the object the way the MISS
    // that stored it did; S3's store time only stands in when nothing was recorded. Note the ETag above
    // stays the bucket's: it is not the upstream's to begin with, and a client sent to the bucket by a
    // 307 is answered by S3 matching that one.
    Headers.LastModified = UpstreamLastModified(response)?.ToString("R") ?? response.LastModified?.ToString("R");
    Headers.ContentLength = response.ContentLength;
    Headers.ContentType = response.Headers.ContentType;
    Headers.ContentEncoding = response.Headers.ContentEncoding;
    Headers.ETag = response.ETag;
    // Only the digest is kept in the bucket: the API-version header is per-response bookkeeping that the
    // /v2/ ping and every live response carry anyway, while the digest belongs to the stored bytes.
    if (response.Metadata[DockerContentDigestMetadataKey] is { Length: > 0 } digest)
      Headers[DockerContentDigestHeader] = digest;
  }

  public async ValueTask InvokeAsync(HttpContext context, CachingRule? rule = null)
  {
    foreach (var (key, value) in Headers)
    {
      if (!ourExcludeHeaders.Contains(key))
        context.Response.Headers[key] = value;
    }
    context.Response.StatusCode = (int)StatusCode;
    SetCachingHeaderFor(context, rule);

    if (Body != null)
      await context.Response.BodyWriter.WriteAsync(Body, context.RequestAborted);
  }

  private static readonly StringValues ourEternalCachingHeader =
    new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365) }.ToString();

  private static readonly StringValues ourPrivateCachingHeader =
    new CacheControlHeaderValue { Private = true, MaxAge = TimeSpan.FromDays(365) }.ToString();

  public static StringValues GetCachingHeader(HttpContext context)
  {
    // Authorized (authenticated) responses are served only to the requesting client, so they
    // must not be stored by shared/intermediary caches. Anonymous responses stay public.
    var isAuthenticated = context.User.Identity?.IsAuthenticated == true;

    // When the request's caching-profile rule gave a freshness window, advertise it as the downstream
    // max-age so shared and browser caches revalidate in step with the proxy instead of holding the
    // artifact for a year. Otherwise (immutable coordinates) keep the eternal 365-day header.
    if (context.Items[CachingProxyConstants.RefreshAfterItemKey] is TimeSpan refreshAfter)
      return new CacheControlHeaderValue { Public = !isAuthenticated, Private = isAuthenticated, MaxAge = refreshAfter }.ToString();

    return isAuthenticated ? ourPrivateCachingHeader : ourEternalCachingHeader;
  }

  public static void SetCachingHeaderFor(HttpContext context, CachingRule? rule = null)
  {
    // For successful (2xx) responses, the cached response is always eternally cacheable.
    if (context.Response.StatusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices)
    {
      context.Response.Headers.CacheControl = GetCachingHeader(context);
    }
    // A 404 a content-negotiated rule produced answers about one representation: our own entry is safe
    // because the key folds in the canonical Accept set (see RemoteProxy.GetCacheVariant), but the edge
    // keys every Accept ordering onto one entry and caches a 4xx on its own account, so one client's
    // negotiation failure was replayed to clients the registry would have answered (MRI-5282). no-store
    // rather than no-cache: it must not be kept at all, not merely revalidated.
    else if (rule is { VaryByAccept: true } && context.Response.StatusCode == StatusCodes.Status404NotFound)
      context.Response.Headers.CacheControl = ourNoStoreHeaderValue;
  }

  private static readonly StringValues ourNoStoreHeaderValue = new CacheControlHeaderValue { NoStore = true }.ToString();

  public static readonly CachedResponse MethodNotAllowed = new(HttpStatusCode.MethodNotAllowed, new HeaderDictionary());
  public static readonly CachedResponse InvalidPath = new(HttpStatusCode.BadRequest, new HeaderDictionary(), [.. "Invalid request path"u8]);
  public static readonly CachedResponse Blacklisted = new(HttpStatusCode.NotFound, new HeaderDictionary(), [.. "Blacklisted"u8]);
}

public class ResponseCache(IFusionCache cache, TimeProvider timeProvider, CachingProxyConfig config)
{
  public ValueTask<CachedResponse?> GetCachedStatusCode(string cacheKey, CancellationToken cancellationToken = default) =>
    cache.GetOrDefaultAsync<CachedResponse>(cacheKey, token: cancellationToken);

  public ValueTask<CachedResponse> PutStatusCode(string cacheKey, HttpStatusCode statusCode, CacheDuration cacheDuration, CancellationToken cancellationToken = default, TimeSpan? maxDuration = null) =>
    PutStatusCode(cacheKey, new CachedResponse(statusCode, new HeaderDictionary()), cacheDuration, cancellationToken, maxDuration);

  /// <summary>
  /// Stores a response head to be replayed for its status's <see cref="CacheDuration"/>.
  /// <para><paramref name="maxDuration"/> is the caller's freshness window (a matched
  /// <see cref="CachingRule.RefreshAfter"/>) and caps every TTL below. A cached entry is replayed without
  /// consulting the storage layer, which is the only place a window is enforced, so an entry that outlives
  /// its window is served stale for the difference: an OCI manifest by tag (5 minutes) stored under a
  /// status TTL of an hour is an hour stale. Null for an immutable path, which has no window to respect.</para>
  /// </summary>
  public async ValueTask<CachedResponse> PutStatusCode(string cacheKey, CachedResponse entry, CacheDuration cacheDuration, CancellationToken cancellationToken = default, TimeSpan? maxDuration = null)
  {
    var cachingTime = CapAt(cacheDuration.GetDuration(entry.StatusCode), maxDuration);
    if (cachingTime == TimeSpan.Zero)
      return entry;

    // L2 (distributed/Redis) TTL is controlled by the global DistributedCacheDuration, but it is never
    // allowed to be shorter than the L1 TTL: the durable backing store must outlive the in-process copy.
    // Capped first, or the window would hold only until L1 eviction and the entry would come back from L2.
    var l2CachingTime = CapAt(config.DistributedCacheDuration.GetDuration(entry.StatusCode), maxDuration);
    var distributedCachingTime = l2CachingTime > cachingTime ? l2CachingTime : cachingTime;
    // Reporting the durable expiration keeps the header in the future for as long as the entry is
    // actually cached, instead of going stale once the in-memory copy is evicted.
    var durableCachingTime = GetDurableDuration(cacheDuration, entry.StatusCode, maxDuration);
    entry.Headers[CachingProxyConstants.CachedStatusHeader] = entry.StatusCode.ToString("D");
    entry.Headers[CachingProxyConstants.CachedUntilHeader] = (timeProvider.GetUtcNow() + durableCachingTime).ToString("R");

    // Duplicated from the defaults rather than constructed fresh: FusionCache uses explicitly-passed
    // options as-is - the defaults apply only when none are given - so a new instance here would drop
    // the L2 hard timeout and background-write settings and put Redis back on the critical path.
    var options = cache.DefaultEntryOptions.Duplicate();
    options.MemoryCacheDuration = cachingTime;
    options.DistributedCacheDuration = distributedCachingTime;
    await cache.SetAsync(cacheKey, entry, options, token: cancellationToken);
    return entry;
  }

  /// <summary>
  /// The duration a cached entry actually remains servable: when a distributed (L2) cache is
  /// wired the entry survives L1 eviction and is re-served from L2 for the (never-shorter) L2
  /// TTL; otherwise it lives only for the L1 TTL. This is the lifetime the Cached-Until header
  /// and any externally-minted handle (e.g. an S3 presigned URL) must be sized against.
  /// <paramref name="maxDuration"/> caps both tiers, as in <see cref="PutStatusCode(string,CachedResponse,CacheDuration,CancellationToken,TimeSpan?)"/>.
  /// </summary>
  public TimeSpan GetDurableDuration(CacheDuration cacheDuration, HttpStatusCode statusCode, TimeSpan? maxDuration = null)
  {
    var cachingTime = CapAt(cacheDuration.GetDuration(statusCode), maxDuration);
    if (!cache.HasDistributedCache)
      return cachingTime;

    var l2CachingTime = CapAt(config.DistributedCacheDuration.GetDuration(statusCode), maxDuration);
    return l2CachingTime > cachingTime ? l2CachingTime : cachingTime;
  }

  private static TimeSpan CapAt(TimeSpan duration, TimeSpan? limit) =>
    limit is { } max && max < duration ? max : duration;
}
