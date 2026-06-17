using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public class S3CachingMiddleware(RequestDelegate requestDelegate, IAmazonS3 amazonS3, CachingProxyConfig config, RemoteProxy remoteProxy, ResponseCache responseCache, TimeProvider timeProvider, ILogger<S3CachingMiddleware>  logger)
{
  private static readonly ByteRange ourPrefetchSize = new(0, 16 * 1024 - 1);

  public async Task InvokeAsync(HttpContext context)
  {
    if (RemoteServers.GetRemoteServer(context, out var remainingPath) is not {} remoteServer)
    {
      await requestDelegate(context);
      return;
    }

    if (!await remoteProxy.ValidateRequestAsync(context))
      return;

    var s3Key = remoteServer.ManglePath(remainingPath);

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
      // Replay the verb-specific head: a HEAD's cached metadata, or a GET's cached redirect.
      if (responseCache.GetCachedStatusCode(verbKey) is { } verbEntry)
      {
        await remoteProxy.SetStatusAsync(context, CachingProxyStatus.HIT, verbEntry);
        return;
      }

      // Nothing known yet for this object: probe S3 with the ranged prefetch (HEAD and GET alike).
      if (responseCache.GetCachedStatusCode(s3Key) == null)
      {
        try
        {
          using var s3Object = await amazonS3.GetObjectAsync(new GetObjectRequest
          {
            BucketName = config.S3!.BucketName,
            Key = s3Key,
            ByteRange = ourPrefetchSize
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
              responseCache.PutStatusCode(s3Key, cachingResponse, remoteServer.CacheDuration));
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
              responseCache.PutStatusCode(verbKey, head, remoteServer.CacheDuration));
            return;
          }

          await RedirectToBucket();
          return;
        }
        catch (AmazonServiceException ex) when (ex.StatusCode is HttpStatusCode.NotFound) { }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Failed to retrieve S3 Object {S3Key}", s3Key);
          throw;
        }
      }

      using var response = await remoteProxy.ProcessAsync(context, s3Key, remoteServer.CacheDuration, remoteServer.GetUpstreamUri(remainingPath));

      // A non-null response is a GET MISS body for us to stream and persist; otherwise it is handled.
      if (response == null) return;

      context.Response.Clear();

      await StoreInBucketAsync(s3Key, response, context.RequestAborted);

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
      logger.LogError(Event.FailedToCacheInS3, e, "Failed to cache {RequestPath} in S3 with {S3Key}", context.Request.Path, s3Key);
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      context.Response.ContentType = MediaTypeNames.Text.Plain;
      await context.Response.WriteAsync("Failed to cache response");
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
                    remoteServer.CacheDuration.GetDuration((HttpStatusCode)context.Response.StatusCode)
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
        Headers =
        {
          Location = location
        }
      };
      // Only a GET redirects (a HEAD is served from memory), so the redirect always belongs under the
      // verb-specific key and is never replayed to a HEAD.
      await remoteProxy.SetStatusAsync(context, CachingProxyStatus.MISS,
        responseCache.PutStatusCode(verbKey, cachingResponse, remoteServer.CacheDuration));
    }
  }

  /// <summary>
  /// Streams the upstream response body into the bucket. S3 PutObject needs a definite
  /// Content-Length: when the upstream declared one we stream the (non-seekable) body straight
  /// through; when it did not (e.g. chunked transfer) we first spool the body to a temp file so the
  /// SDK gets a real length and does not buffer the whole body in memory.
  /// </summary>
  private async Task StoreInBucketAsync(string s3Key, HttpResponseMessage response, CancellationToken cancellationToken)
  {
    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
    var contentLength = response.Content.Headers.ContentLength;

    string? spoolPath = null;
    FileStream? spooled = null;
    try
    {
      var uploadStream = body;
      if (contentLength is null)
      {
        spoolPath = Path.Combine(Path.GetTempPath(), "s3-upload-" + Guid.NewGuid());
        spooled = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
        await body.CopyToAsync(spooled, cancellationToken);
        spooled.Position = 0;
        contentLength = spooled.Length;
        uploadStream = spooled;
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
          ["uri"] = response.RequestMessage?.RequestUri?.ToString()
        },
        InputStream = uploadStream,
      }, cancellationToken);
    }
    finally
    {
      if (spooled != null) await spooled.DisposeAsync();
      if (spoolPath != null) File.Delete(spoolPath);
    }
  }

  public class HealthCheck(IAmazonS3 amazonS3, CachingProxyConfig config) : IHealthCheck
  {
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
      CancellationToken cancellationToken)
    {
      try
      {
        var bucketAcl = await amazonS3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = config.S3?.BucketName },
          cancellationToken);
        if (bucketAcl.HttpStatusCode == HttpStatusCode.OK)
          return HealthCheckResult.Healthy(config.S3?.BucketName);
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception e)
      {
        return HealthCheckResult.Unhealthy(e.Message);
      }

      return HealthCheckResult.Unhealthy();
    }
  }
}
