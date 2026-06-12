using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "UnusedMember.Global")]
public class CachingProxy(
  CacheFileProvider cacheFileProvider,
  ILogger<CachingProxy> logger,
  CachingProxyConfig config,
  StaticFileOptions options,
  RemoteProxy remoteProxy)
  : IMiddleware, IHealthCheck
{
  private const int BUFFER_SIZE = 81920;

  public async Task InvokeAsync(HttpContext context, RequestDelegate next)
  {
    var requestPath = context.Request.Path;
    var remoteServer = remoteProxy.LookupRemoteServer(requestPath, out var remainingPath);
    if (remoteServer == null)
    {
      await next(context);
      return;
    }

    using var response = await remoteProxy.ProcessAsync(context, remoteServer, remainingPath, _ =>
      options.ContentTypeProvider.TryGetContentType(requestPath.Value ?? "", out var resolvedContentType) ?
        resolvedContentType : options.DefaultContentType);

    // A non-null response is a GET MISS body for us to stream and persist; otherwise it is handled.
    if (response == null) return;

    var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
    var contentLength = response.Content.Headers.ContentLength;
    var contentLastModified = response.Content.Headers.LastModified;

    var cachePath = cacheFileProvider.GetFutureCacheFileLocation(remoteServer, remainingPath, contentEncoding);
    if (cachePath == null)
    {
      await remoteProxy.SetStatusAsync(context, CachingProxyStatus.BAD_REQUEST, HttpStatusCode.BadRequest, "Invalid cache path");
      return;
    }

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
        logger.LogWarning(Event.NotMatchedContentLength, "Expected {ContentLength} bytes from Content-Length, but downloaded {Length}: {RequestPath}",
          contentLength, tempFileInfo.Length, requestPath);
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
  }

  private void CatchSilently(Action action)
  {
    try
    {
      action();
    }
    catch (Exception e)
    {
      logger.Log(LogLevel.Error, e, "LogSilently: {Message}", e.Message);
    }
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

  public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    var localCachePath = config.LocalCachePath;
    var minimumFreeDiskSpaceMb = config.MinimumFreeDiskSpaceMb;
    var availableFreeSpaceMb = new DriveInfo(localCachePath).AvailableFreeSpace / (1024 * 1024);
    if (availableFreeSpaceMb < minimumFreeDiskSpaceMb)
    {
      logger.LogError(Event.NotEnoughFreeDiskSpace,
        "Not Enough Free Disk Space. {AvailableFreeSpaceMb} MB is free at {LocalCachePath}, but minimum is {MinimumFreeDiskSpaceMb} MB",
        availableFreeSpaceMb, localCachePath, minimumFreeDiskSpaceMb);
      return Task.FromResult(HealthCheckResult.Unhealthy(
        $"Not Enough Free Disk Space. {availableFreeSpaceMb} MB is free at {localCachePath}, but minimum is {minimumFreeDiskSpaceMb} MB"));
    }

    return Task.FromResult(HealthCheckResult.Healthy());
  }
}
