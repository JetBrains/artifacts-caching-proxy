using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public class S3CachingMiddleware(RequestDelegate requestDelegate, IAmazonS3 amazonS3, CachingProxyConfig config, RemoteProxy remoteProxy, ResponseCache responseCache, TimeProvider timeProvider, ILogger<S3CachingMiddleware>  logger)
{
  // The ranged-probe window: a body that fits is served inline, a larger one is redirected. Sized from
  // S3.InlineThresholdBytes (default 32 KiB). config.S3 is non-null here — the middleware is only wired
  // when S3.BucketName is set (see Program.ConfigureOurApp).
  private readonly ByteRange myPrefetchSize = new(0, config.S3!.InlineThresholdBytes - 1);

  // Single-flight coalescing of S3 misses, partitioned by the object key's "aa/bb" prefix (see
  // ManglePath) rather than by the full key. Each prefix-partition is a single-permit concurrency
  // limiter, so at most one request per prefix probes/fetches/uploads at a time while the rest wait
  // and then serve from cache; different prefixes run freely. This both de-duplicates concurrent
  // requests for the same object (same key => same prefix) AND bounds concurrent work per S3
  // prefix-partition, the unit S3 throttles on. Because the keys are sha256-spread, distinct hot
  // objects rarely share a prefix, so the coarser locking seldom serializes unrelated objects. An
  // unbounded queue means contenders wait their turn rather than being rejected; the partitioned
  // limiter reclaims idle partitions on its own. The middleware is a singleton (UseMiddleware), so
  // this single instance is shared across all requests for the app's lifetime.
  private readonly PartitionedRateLimiter<string> myKeyLocks = PartitionedRateLimiter.Create<string, int>(
    static s3Key => RateLimitPartition.GetConcurrencyLimiter(PrefixPartition(s3Key), static _ => new ConcurrencyLimiterOptions
    {
      PermitLimit = 1,
      QueueLimit = int.MaxValue,
      QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    }));

  // s3Key is "aa/bb/<lowercase-hex-hash>" (see ManglePath). Fold its two prefix bytes "aa" and "bb"
  // into a 0..65535 partition id so partitioning allocates no per-request substring.
  private static int PrefixPartition(string s3Key)
  {
    static int Nibble(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
    return (Nibble(s3Key[0]) << 12) | (Nibble(s3Key[1]) << 8) | (Nibble(s3Key[3]) << 4) | Nibble(s3Key[4]);
  }

  public async Task InvokeAsync(HttpContext context)
  {
    if (RemoteServers.GetRemoteServer(context, out var remainingPath) is not {} remoteServer)
    {
      await requestDelegate(context);
      return;
    }

    var upstreamUri = await remoteProxy.ValidateRequestAsync(context, remoteServer, remainingPath);
    if (upstreamUri == null)
      return;

    var s3Key = upstreamUri.ManglePath();

    // A HEAD is always answered from memory with the object's metadata (never redirected), and a
    // redirect is therefore produced only for a GET. So the verb-specific key holds, per verb, a
    // HEAD's large-object metadata head or a GET's (signed or unsigned) redirect — replayed only for
    // the verb that produced it. The shared key holds the verb-agnostic values: inline bodies (plain
    // bytes) and negative results. So a HEAD that prefetches a small object warms the following GET,
    // and a missing object is probed/cached once for both verbs.
    var verbKey = context.Request.Method + s3Key;
    var isHead = HttpMethods.IsHead(context.Request.Method);

    try
    {
      // Replay the verb-specific head: a HEAD's cached metadata, or a GET's cached redirect. Kept
      // outside the per-key lock so steady-state HITs never contend on the semaphore.
      if (await responseCache.GetCachedStatusCode(verbKey, context.RequestAborted) is { } verbEntry)
      {
        await remoteProxy.SetStatusAsync(context, CachingProxyStatus.HIT, verbEntry);
        return;
      }

      // Coalesce concurrent misses: only one request per S3 prefix-partition probes S3, fetches
      // upstream and uploads, while the rest wait here and then serve from the now-populated cache —
      // this is what keeps a thundering herd from amplifying into many GetObject/PutObject calls
      // against one prefix (S3 SlowDown). Pass the verb-agnostic s3Key (the limiter partitions it down
      // to its "aa/bb" prefix) so a concurrent HEAD and GET share one probe; the only verb-specific
      // outputs (large-object HEAD head, GET redirect) are cheap local work.
      // The unbounded queue guarantees the permit is granted, so the lease is always acquired.
      using var keyLock = await myKeyLocks.AcquireAsync(s3Key, permitCount: 1, context.RequestAborted);

      // Re-check after acquiring (double-checked locking): a prior leader may have populated the
      // verb-specific head (a large-object redirect or HEAD metadata) while we waited.
      if (await responseCache.GetCachedStatusCode(verbKey, context.RequestAborted) is { } cachedVerbEntry)
      {
        await remoteProxy.SetStatusAsync(context, CachingProxyStatus.HIT, cachedVerbEntry);
        return;
      }

      // Nothing known yet for this object: probe S3 with the ranged prefetch (HEAD and GET alike).
      // The shared-key check also serves as the post-lock re-check for the verb-agnostic values (a
      // leader's inlined small object or negative result), so waiters serve those without re-probing.
      if (await responseCache.GetCachedStatusCode(s3Key, context.RequestAborted) == null)
      {
        try
        {
          using var s3Object = await amazonS3.GetObjectAsync(new GetObjectRequest
          {
            BucketName = config.S3!.BucketName,
            Key = s3Key,
            ByteRange = myPrefetchSize
          }, context.RequestAborted);

          // Did the probe return the whole object, or only the first slice? Decide purely from
          // Content-Range ("bytes <from>-<to>/<total>"), whose semantics are unambiguous: the bytes
          // returned are (to - from + 1) and the object's full size is the total.
          // When the returned range spans the whole object it fit the window, so
          // inline it (shared by both verbs); otherwise we only got a slice and must redirect.
          if (ContentRangeHeaderValue.TryParse(s3Object.ContentRange, out var contentRange)
              && contentRange is { HasRange: true, HasLength: true }
              && contentRange.To - contentRange.From + 1 == contentRange.Length)
          {
            var body = new byte[contentRange.To!.Value - contentRange.From!.Value + 1];
            await s3Object.ResponseStream.ReadExactlyAsync(body, 0, body.Length, context.RequestAborted);

            var cachingResponse = new CachedResponse(s3Object) { StatusCode = HttpStatusCode.OK, Body = body };

            await remoteProxy.SetStatusAsync(context, CachingProxyStatus.MISS,
              await responseCache.PutStatusCode(s3Key, cachingResponse, remoteServer.CacheDuration, context.RequestAborted));
            return;
          }

          // Too large to inline. A HEAD is answered from memory with the object's metadata: the body
          // is omitted but Content-Length is the full object size from Content-Range (not the
          // prefetched slice). A GET is redirected to S3.
          if (isHead && contentRange is { HasLength: true })
          {
            var head = new CachedResponse(s3Object)
            {
              StatusCode = HttpStatusCode.OK,
              Headers =
              {
                ContentLength = contentRange.Length
              }
            };
            await remoteProxy.SetStatusAsync(context, CachingProxyStatus.MISS,
              await responseCache.PutStatusCode(verbKey, head, remoteServer.CacheDuration, context.RequestAborted));
            return;
          }

          await RedirectToBucket();
          return;
        }
        catch (AmazonServiceException ex) when (ex.StatusCode is HttpStatusCode.NotFound) { }
      }

      using var response = await remoteProxy.ProcessAsync(context, s3Key, remoteServer.CacheDuration, upstreamUri, auth: remoteServer.Auth);

      // A non-null response is a GET MISS body for us to stream and persist; otherwise it is handled.
      if (response == null) return;

      context.Response.Clear();

      await StoreInBucketAsync(s3Key, upstreamUri, response, context.RequestAborted);

      await RedirectToBucket();
    }
    catch (OperationCanceledException)
    {
      context.Abort();
    }
    catch (Exception e)
    {
      // The artifact exists upstream; we just failed to store or redirect it. Respond 503 (and do
      // NOT cache a negative result) so the client retries and a transient S3 problem recovers on
      // the next request, instead of serving a "not found" for an artifact that is actually available.
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      context.Response.ContentType = MediaTypeNames.Text.Plain;
      await context.Response.WriteAsync("Failed to cache response");

      switch (e)
      {
        case AmazonServiceException ase:
          logger.LogError(Event.FailedToCacheInS3,"Failed to cache {RequestPath} with S3 error {s3ErrorCode}: {s3ErrorMessage}", context.Request.Path, ase.ErrorCode, ase.Message);
          break;
        default:
          logger.LogError(Event.FailedToCacheInS3, e, "Failed to cache {RequestPath}", context.Request.Path);
          break;
      }
    }

    return;

    async ValueTask RedirectToBucket()
    {
      string location;
      if (config.S3!.SignedLinks)
      {
        location = await amazonS3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
          BucketName = config.S3.BucketName,
          Key = s3Key,
          Verb = HttpMethods.IsHead(context.Request.Method) ? HttpVerb.HEAD : HttpVerb.GET,
          Expires = timeProvider.GetUtcNow().UtcDateTime +
                    config.S3.CacheOffsetDuration +
                    responseCache.GetDurableDuration(remoteServer.CacheDuration, (HttpStatusCode)context.Response.StatusCode)
        });
      }
      else
      {
        var endpoint = amazonS3.Config.DetermineServiceOperationEndpoint(
          new ServiceOperationEndpointParameters(new GetObjectRequest
          {
            BucketName = config.S3.BucketName,
            Key = s3Key,
          }));
        // Join with exactly one '/' and percent-encode each key segment (the path may contain
        // spaces or other reserved characters), keeping the '/' separators intact.
        var baseUrl = endpoint.URL.EndsWith('/') ? endpoint.URL : endpoint.URL + "/";
        location = baseUrl + s3Key;
      }

      var cachingResponse = new CachedResponse(HttpStatusCode.RedirectKeepVerb, new HeaderDictionary())
      {
        Headers = { Location = location }
      };
      // Only a GET redirects (a HEAD is served from memory), so the redirect always belongs under the
      // verb-specific key and is never replayed to a HEAD.
      await remoteProxy.SetStatusAsync(context, CachingProxyStatus.MISS,
        await responseCache.PutStatusCode(verbKey, cachingResponse, remoteServer.CacheDuration, context.RequestAborted));
    }
  }

  /// <summary>
  /// Streams the upstream response body into the bucket. S3 PutObject needs a definite
  /// Content-Length: when the upstream declared one we stream the (non-seekable) body straight
  /// through; when it did not (e.g. chunked transfer) we first spool the body to a temp file so the
  /// SDK gets a real length and does not buffer the whole body in memory.
  /// </summary>
  private async Task StoreInBucketAsync(string s3Key, Uri requestUri, HttpResponseMessage response, CancellationToken cancellationToken)
  {
    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
    var contentLength = response.Content.Headers.ContentLength;

    var uploadStream = Stream.Null;
    try
    {
      if (contentLength is null)
      {
        var tmpPath = Path.Combine(Path.GetTempPath(), "s3-upload-" + Guid.NewGuid());
        uploadStream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await body.CopyToAsync(uploadStream, cancellationToken);
        uploadStream.Position = 0;
        contentLength = uploadStream.Length;
      }
      else
      {
        uploadStream = body;
      }

      await amazonS3.PutObjectAsync(new PutObjectRequest
      {
        BucketName = config.S3!.BucketName,
        Key = s3Key,
        Headers =
        {
          ContentType = response.Content.Headers.ContentType?.ToString(),
          ContentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault(),
          ContentLength = contentLength.Value,
        },
        Metadata =
        {
          ["uri"] = requestUri.ToString()
        },
        InputStream = uploadStream,
      }, cancellationToken);
    }
    finally
    {
      await uploadStream.DisposeAsync();
    }
  }
}
