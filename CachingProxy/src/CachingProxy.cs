using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "UnusedMember.Global")]
public class CachingProxy
{
  private readonly RequestDelegate myRequestDelegate;
  private readonly ILogger<CachingProxy> myLogger;
  private readonly IContentTypeProvider myContentTypeProvider;
  private readonly RemoteProxy myRemoteProxy;
  private readonly string myLocalCachePath;

  private const int BUFFER_SIZE = 81920;

  public CachingProxy(
    RequestDelegate requestDelegate,
    ILogger<CachingProxy> logger,
    CachingProxyConfig config,
    IContentTypeProvider contentTypeProvider,
    RemoteProxy remoteProxy)
  {
    myLocalCachePath = config.LocalCachePath;
    if (string.IsNullOrEmpty(myLocalCachePath))
      throw new ArgumentNullException(nameof(myLocalCachePath), "LocalCachePath could not be null");
    if (!Directory.Exists(myLocalCachePath))
    {
      if (myLocalCachePath.StartsWith(Path.GetTempPath()))
        Directory.CreateDirectory(myLocalCachePath);
      else
        throw new ArgumentException("LocalCachePath doesn't exist: " + myLocalCachePath);
    }

    myRequestDelegate = requestDelegate;
    myLogger = logger;
    myContentTypeProvider = contentTypeProvider;
    myRemoteProxy = remoteProxy;
  }

  public async Task InvokeAsync(HttpContext context)
  {
    if (RemoteServers.GetRemoteServer(context, out var remainingPath) is not {} remoteServer)
    {
      await myRequestDelegate(context);
      return;
    }

    if (!await myRemoteProxy.ValidateRequestAsync(context))
      return;

    IResult? cachedFileResult = null;
    string? cachedContentEncoding = null;
    CatchSilently(() =>
    {
      var contentType = GetContentType(remainingPath);
      foreach (var contentEncoding in GetCacheLookupContentEncodings(context))
      {
        var cachedFile = Path.Combine(myLocalCachePath,
          CacheFileProvider.GetFutureCacheFileLocation(remoteServer, remainingPath!, contentEncoding == null ? null : new StringSegment(contentEncoding)));
        if (File.Exists(cachedFile))
        {
          cachedContentEncoding = contentEncoding;
          cachedFileResult = TypedResults.PhysicalFile(cachedFile, contentType, enableRangeProcessing: true);
          break;
        }
      }
    });
    if (cachedFileResult != null)
    {
      myRemoteProxy.SetStatusHeader(context, CachingProxyStatus.HIT);
      context.Response.Headers.CacheControl = RemoteProxy.OurEternalCachingHeader;
      if (cachedContentEncoding != null)
        context.Response.Headers.ContentEncoding = cachedContentEncoding;
      await cachedFileResult.ExecuteAsync(context);
      return;
    }

    using var response = await myRemoteProxy.ProcessAsync(context, remoteServer, remainingPath, GetContentType(remainingPath));

    // A non-null response is a GET MISS body for us to stream and persist; otherwise it is handled.
    if (response == null) return;

    var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
    var contentLength = response.Content.Headers.ContentLength;
    var contentLastModified = response.Content.Headers.LastModified;

    var cachePath = Path.Combine(myLocalCachePath, CacheFileProvider.GetFutureCacheFileLocation(remoteServer, remainingPath, contentEncoding));
    var tempFile = cachePath + ".tmp." + Guid.NewGuid();
    try
    {
      var parent = Directory.GetParent(cachePath);
      Directory.CreateDirectory(parent!.FullName);

      await using (var stream = new FileStream(
                     tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, BUFFER_SIZE,
                     FileOptions.Asynchronous))
      {
        await using (var sourceStream = await response.Content.ReadAsStreamAsync())
          await CopyToTwoStreamsAsync(sourceStream, context.Response.Body, stream, context.RequestAborted);
      }

      var tempFileInfo = new FileInfo(tempFile);
      if (contentLength != null && tempFileInfo.Length != contentLength)
      {
        myLogger.LogWarning(Event.NotMatchedContentLength, "Expected {ContentLength} bytes from Content-Length, but downloaded {Length}: {RequestPath}",
          contentLength, tempFileInfo.Length, context.Request.Path);
        context.Abort();
        return;
      }

      if (contentLastModified.HasValue)
        File.SetLastWriteTimeUtc(tempFile, contentLastModified.Value.UtcDateTime);

      try
      {
        File.Move(tempFile, cachePath);
      }
      catch (IOException)
      {
        if (File.Exists(cachePath))
        {
          // It's ok, a parallel request cached it before us
        }
        else throw;
      }
    }
    catch (OperationCanceledException)
    {
      // Probable cause: OperationCanceledException while streaming the upstream response body
      // Probable cause: OperationCanceledException from this service's client (context.RequestAborted)

      // ref: https://github.com/aspnet/StaticFiles/commit/bbf1478821c11ecdcad776dad085d6ee09d8f8ee#diff-991aec26255237cd6dbfa787d0995a2aR85
      // ref: https://github.com/aspnet/StaticFiles/issues/150

      // Don't throw this exception, it's most likely caused by the client disconnecting.
      // However, if it was cancelled for any other reason we need to prevent empty responses.
      context.Abort();
    }
    finally
    {
      CatchSilently(() =>
      {
        if (File.Exists(tempFile))
          File.Delete(tempFile);
      });
    }

    string GetContentType(string? path)
    {
      return myContentTypeProvider.TryGetContentType(path ?? "", out var resolvedContentType) ?
        resolvedContentType : MediaTypeNames.Application.Octet;
    }
  }

  private void CatchSilently(Action action)
  {
    try
    {
      action();
    }
    catch (Exception e)
    {
      myLogger.Log(LogLevel.Error, e, "LogSilently: {Message}", e.Message);
    }
  }

  // Only plain and gzip variants are ever stored on disk, so there is no point probing for any other
  // Accept-Encoding token. Prefer the gzip variant when the client asked for it; otherwise prefer
  // plain but still fall back to gzip (a gzip-only cache entry is served to every client, matching
  // the previous static-file behavior).
  private static IEnumerable<string?> GetCacheLookupContentEncodings(HttpContext context)
  {
    var acceptsGzip = context.Request.GetTypedHeaders().AcceptEncoding
      .Any(headerValue => string.Equals(headerValue.Value.Value, "gzip", StringComparison.OrdinalIgnoreCase));
    return acceptsGzip ? ["gzip", null] : [null, "gzip"];
  }

  private static async Task CopyToTwoStreamsAsync(Stream source, Stream dest1, FileStream dest2, CancellationToken cancellationToken)
  {
    using var buffer = MemoryPool<byte>.Shared.Rent(BUFFER_SIZE);
    var memory = buffer.Memory;
    while (true)
    {
      var length = await source.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
      if (length == 0)
        break;

      await dest1.WriteAsync(memory[..length], cancellationToken).ConfigureAwait(false);
      await dest2.WriteAsync(memory[..length], cancellationToken).ConfigureAwait(false);
    }
  }

  public class HealthCheck(CachingProxyConfig config) : IHealthCheck
  {
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
      var localCachePath = config.LocalCachePath;
      var minimumFreeDiskSpaceMb = config.MinimumFreeDiskSpaceMb;
      var availableFreeSpaceMb = new DriveInfo(localCachePath).AvailableFreeSpace / (1024 * 1024);
      if (availableFreeSpaceMb < minimumFreeDiskSpaceMb)
      {
        return Task.FromResult(HealthCheckResult.Unhealthy(
          $"Not Enough Free Disk Space. {availableFreeSpaceMb} MB is free at {localCachePath}, but minimum is {minimumFreeDiskSpaceMb} MB"));
      }

      return Task.FromResult(HealthCheckResult.Healthy());
    }
  }
}
