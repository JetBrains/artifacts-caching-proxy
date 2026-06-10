using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
public class S3CachingMiddleware(IAmazonS3 amazonS3, CachingProxyConfig config, RemoteProxy remoteProxy, ResponseCache responseCache, TimeProvider timeProvider, ILogger<S3CachingMiddleware>  logger) : IMiddleware, IHealthCheck
{
  public async Task InvokeAsync(HttpContext context, RequestDelegate next)
  {
    var remoteServer = remoteProxy.LookupRemoteServer(context.Request.Path, out var remainingPath);
    if (remoteServer == null)
    {
      await next(context);
      return;
    }

    // Validate method/path before probing S3 or the in-memory cache, so an invalid path or a
    // non-GET/HEAD method can't be redirected to the bucket unchecked.
    if (!await remoteProxy.ValidateRequestAsync(context)) return;

    var requestPath = context.Request.Path.Value!;
    var s3Key = requestPath[1..];

    try
    {
      if (responseCache.GetCachedStatusCode(requestPath) == null)
      {
        try
        {
          await amazonS3.GetObjectMetadataAsync(config.S3!.BucketName, s3Key, context.RequestAborted);
          await RedirectToBucket();
          return;
        }
        catch (AmazonServiceException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
      }

      using var response = await remoteProxy.ProcessAsync(context, remoteServer, remainingPath);

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
      logger.LogError(e, "Failed to cache response");
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
          Expires = timeProvider.GetUtcNow().UtcDateTime + CacheDuration.CacheOffsetDuration +
                    ResponseCache.GetCacheDuration(remoteServer.CacheDuration, HttpStatusCode.RedirectKeepVerb)
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
        location = endpoint.URL + s3Key;
      }

      IHeaderDictionary headers = new HeaderDictionary();
      headers.Location = location;
      var cachingResponse = new CachedResponse(HttpStatusCode.RedirectKeepVerb, headers);
      remoteProxy.SetStatus(context, CachingProxyStatus.MISS,
        responseCache.PutStatusCode(requestPath, remoteServer.CacheDuration, cachingResponse));
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

  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    var bucketAcl = await amazonS3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = config.S3?.BucketName }, cancellationToken);

    return bucketAcl.HttpStatusCode == HttpStatusCode.OK ? HealthCheckResult.Healthy() :
      HealthCheckResult.Unhealthy("Failed to retrieve bucket ACL");
  }
}
