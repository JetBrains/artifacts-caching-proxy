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

    var upstreamUri = remoteServer.GetUpstreamUri(remainingPath);
    var s3Key = upstreamUri.ToKey();

    // The only verb-bound cached value is a presigned redirect (signed for a specific verb), so it
    // lives under a per-method key and is replayed only for the matching verb. Everything else is
    // verb-agnostic and shares one key: inline bodies (plain bytes), negative results, upstream HEAD
    // metadata and unsigned bucket redirects. So a HEAD that prefetches a small object warms the
    // following GET, and a missing object is probed/cached once for both verbs.
    var signedRedirectKey = context.Request.Method + s3Key;

    try
    {
      // A signed presigned redirect is the only per-verb entry; replay it for the matching verb.
      if (config.S3!.SignedLinks
          && responseCache.GetCachedStatusCode(signedRedirectKey) is { } signedRedirect)
      {
        await remoteProxy.SetStatusAsync(context, CachingProxyStatus.HIT, signedRedirect);
        return;
      }

      // Nothing known yet for this object: probe S3 with the ranged prefetch (HEAD and GET alike).
      if (responseCache.GetCachedStatusCode(s3Key) == null)
      {
        try
        {
          using var s3Object = await amazonS3.GetObjectAsync(new GetObjectRequest
          {
            BucketName = config.S3.BucketName,
            Key = s3Key,
            ByteRange = ourPrefetchSize
          }, context.RequestAborted);

          // strictly less: the whole object fit in the prefetch window, so inline it.
          if (ContentRangeHeaderValue.TryParse(s3Object.ContentRange, out var contentRange) && contentRange.To < ourPrefetchSize.End && contentRange.HasLength)
          {
            var body = new byte[contentRange.Length!.Value];
            await s3Object.ResponseStream.ReadExactlyAsync(body, 0, body.Length, context.RequestAborted);

            var cachingResponse = new CachedResponse(s3Object) { StatusCode = HttpStatusCode.OK, Body = body };

            await remoteProxy.SetStatusAsync(context, CachingProxyStatus.MISS,
              responseCache.PutStatusCode(s3Key, cachingResponse, remoteServer.CacheDuration));
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

      using var response = await remoteProxy.ProcessAsync(context, s3Key, remoteServer.CacheDuration, upstreamUri);

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
        location = baseUrl + string.Join('/', s3Key.Split('/').Select(Uri.EscapeDataString));
      }

      var cachingResponse = new CachedResponse(HttpStatusCode.RedirectKeepVerb, new HeaderDictionary())
      {
        Headers =
        {
          Location = location
        }
      };
      // A signed redirect is verb-bound, so it goes under the per-method key; an unsigned bucket
      // redirect is verb-agnostic and shares the common key.
      var redirectCacheKey = config.S3!.SignedLinks ? signedRedirectKey : s3Key;
      await remoteProxy.SetStatusAsync(context, CachingProxyStatus.MISS,
        responseCache.PutStatusCode(redirectCacheKey, cachingResponse, remoteServer.CacheDuration));
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
          ContentLength = contentLength.Value
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
