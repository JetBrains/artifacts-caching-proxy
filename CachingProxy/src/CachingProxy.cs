using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "UnusedMember.Global")]
public class CachingProxy
{
  private readonly RequestDelegate myRequestDelegate;
  private readonly ILogger<CachingProxy> myLogger;
  private readonly IContentTypeProvider myContentTypeProvider;
  private readonly RemoteProxy myRemoteProxy;
  private readonly ResponseCache myResponseCache;
  private readonly TimeProvider myTimeProvider;
  private readonly string myLocalCachePath;

  private const int BUFFER_SIZE = 81920;

  public CachingProxy(
    RequestDelegate requestDelegate,
    ILogger<CachingProxy> logger,
    CachingProxyConfig config,
    IContentTypeProvider contentTypeProvider,
    RemoteProxy remoteProxy,
    ResponseCache responseCache,
    TimeProvider timeProvider)
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
    myResponseCache = responseCache;
    myTimeProvider = timeProvider;
  }

  public async Task InvokeAsync(HttpContext context)
  {
    if (RemoteServers.GetRemoteServer(context, out var remainingPath) is not {} remoteServer)
    {
      await myRequestDelegate(context);
      return;
    }

    var upstreamUri = await myRemoteProxy.ValidateRequestAsync(context, remoteServer, remainingPath);
    if (upstreamUri == null)
      return;

    var contentType = myContentTypeProvider.TryGetContentType(remainingPath ?? "", out var resolvedContentType) ?
      resolvedContentType : MediaTypeNames.Application.Octet;

    // The caching-profile rule for this path decides how it is cached: a freshness window (revalidate
    // when older) or an always-redirect. A null rule (no profile / no match) means cache forever. The
    // freshness window is also advertised to the client as Cache-Control max-age (see
    // ResponseCache.GetCachingHeader), so it is stashed for the whole request here.
    var rule = remoteServer.Profile?.Match(context.Request.Path.Value ?? "");
    if (rule?.RefreshAfter is { } window)
      context.Items[CachingProxyConstants.RefreshAfterItemKey] = window;

    try
    {
      foreach (var contentEncoding in GetCacheLookupContentEncodings(context))
      {
        var cachedFile = new FileInfo(Path.Combine(myLocalCachePath, upstreamUri.GetFutureCacheFileLocation(contentEncoding)));
        if (!cachedFile.Exists)
          continue;

        // Past its freshness window: revalidate against the upstream before serving (200 replaces,
        // 304 keeps, 404 deletes, any other error serves the stale copy). When the rule sets no
        // freshness window the stored copy is fresh forever, exactly as before — and costs no extra
        // stat, since only a windowed rule reads the stamp.
        if (rule?.RefreshAfter is { } refreshAfter)
        {
          var storedDate = GetStoredDate(cachedFile.FullName);
          // A missing stamp (a file cached before this proxy stamped them, or one dropped out of
          // band) counts as stale: one conditional GET re-establishes the window, which is far
          // cheaper than serving a copy of unbounded age.
          if (storedDate == null || myTimeProvider.GetUtcNow().UtcDateTime - storedDate.Value > refreshAfter)
          {
            await RevalidateAndServeAsync(context, remoteServer, upstreamUri, contentType, contentEncoding, cachedFile, storedDate);
            return;
          }
        }

        await ServeFileAsync(context, cachedFile, contentType, contentEncoding, CachingProxyStatus.HIT);
        return;
      }

      using var response = await myRemoteProxy.ProcessAsync(context, upstreamUri.ManglePath(),
        remoteServer.CacheDuration, upstreamUri, contentType, remoteServer.Auth, rule);

      // A non-null response is a GET MISS body for us to stream and persist; otherwise it is handled.
      if (response == null) return;

      var storedPath = await DownloadToDiskAsync(context, response, upstreamUri);
      // Anchor the freshness window at the moment we stored it, so the copy counts as fresh until the
      // window elapses. Only windowed rules need a stamp, so artifacts cached forever get no extra file.
      if (storedPath != null && rule?.RefreshAfter != null)
        TouchStoredDate(storedPath);
    }
    catch (OperationCanceledException)
    {
      // Probable cause: OperationCanceledException while streaming the upstream response body
      // Probable cause: OperationCanceledException from this service's client (context.RequestAborted)
      // However, if it was cancelled for any other reason, we need to prevent empty responses.
      context.Abort();
    }
  }

  private async Task ServeFileAsync(HttpContext context, FileInfo cachedFile, string contentType, string? contentEncoding, CachingProxyStatus status)
  {
    myRemoteProxy.SetStatusHeader(context, status);
    CachedResponse.SetCachingHeaderFor(context);
    if (contentEncoding != null)
      context.Response.Headers.ContentEncoding = contentEncoding;
    await TypedResults
      .PhysicalFile(cachedFile.FullName, contentType, enableRangeProcessing: true)
      .ExecuteAsync(context);
  }

  private async Task RevalidateAndServeAsync(HttpContext context, RemoteServers.RemoteServer remoteServer,
    Uri upstreamUri, string contentType, string? contentEncoding, FileInfo cachedFile, DateTime? storedDate)
  {
    // Disk keeps no ETag, so revalidate with If-Modified-Since from the file's stored date (reset on
    // each store/revalidation to restart the freshness window). A null stored date means we have no
    // stamp to validate against, so the request goes out unconditionally and upstream answers with a
    // full body (Replaced), which re-stamps the entry.
    var result = await myRemoteProxy.RevalidateAsync(context, upstreamUri, etag: null, storedDate, remoteServer.Auth, context.RequestAborted);

    switch (result.Outcome)
    {
      case RevalidationOutcome.NotModified:
        // Still valid: reset the stored date so the window restarts, then serve the existing copy.
        TouchStoredDate(cachedFile.FullName);
        await ServeFileAsync(context, cachedFile, contentType, contentEncoding, CachingProxyStatus.REVALIDATED);
        return;

      case RevalidationOutcome.Gone:
        // Removed upstream: drop both encoding variants and cache+serve the 404, matching a MISS 404.
        DeleteCachedVariants(upstreamUri);
        await myRemoteProxy.SetStatusAsync(context, CachingProxyStatus.NEGATIVE_MISS,
          await myResponseCache.PutStatusCode(upstreamUri.ManglePath(), HttpStatusCode.NotFound, remoteServer.CacheDuration, context.RequestAborted));
        return;

      case RevalidationOutcome.UpstreamError:
        // Could not reach/validate the upstream: keep and serve the stale copy.
        await ServeFileAsync(context, cachedFile, contentType, contentEncoding, CachingProxyStatus.STALE);
        return;

      case RevalidationOutcome.Replaced:
        using (var response = result.Response!)
        {
          // Write the (revalidated) response head, then stream+persist the new body.
          await myRemoteProxy.SetStatusAsync(context, CachingProxyStatus.REVALIDATED,
            new CachedResponse(response) { Headers = { ContentType = contentType } });

          var newPath = await DownloadToDiskAsync(context, response, upstreamUri);
          if (newPath == null) return;

          // The new representation may use a different encoding (hence a different path) than the
          // stale variant we matched; remove the now-orphaned old variant and its stamp.
          if (!string.Equals(newPath, cachedFile.FullName, StringComparison.Ordinal))
          {
            CatchSilently(() => { if (cachedFile.Exists) cachedFile.Delete(); });
            DeleteStoredDate(cachedFile.FullName);
          }

          TouchStoredDate(newPath);
        }
        return;
    }
  }

  // Streams the upstream body to the client while persisting it to the local cache via an atomic
  // temp-file + move, validating Content-Length. Returns the final cache file path, or null when the
  // download was aborted (content-length mismatch or cancellation). Leaves no orphaned temp file.
  private async Task<string?> DownloadToDiskAsync(HttpContext context, HttpResponseMessage response, Uri upstreamUri)
  {
    var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
    var contentLength = response.Content.Headers.ContentLength;
    var contentLastModified = response.Content.Headers.LastModified;

    var cachedFile = Path.Combine(myLocalCachePath, upstreamUri.GetFutureCacheFileLocation(contentEncoding));
    var tempFile = cachedFile + ".tmp." + Guid.NewGuid();

    var parent = Directory.GetParent(cachedFile);
    Directory.CreateDirectory(parent!.FullName);

    try
    {
      await using (var stream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, BUFFER_SIZE, FileOptions.Asynchronous))
      {
        await using (var sourceStream = await response.Content.ReadAsStreamAsync(context.RequestAborted))
          await CopyToTwoStreamsAsync(sourceStream, context.Response.Body, stream, context.RequestAborted);
      }

      var tempFileInfo = new FileInfo(tempFile);
      if (contentLength != null && tempFileInfo.Length != contentLength)
      {
        myLogger.LogWarning(Event.NotMatchedContentLength, "Expected {ContentLength} bytes from Content-Length, but downloaded {Length}: {RequestPath}",
          contentLength, tempFileInfo.Length, context.Request.Path);
        context.Abort();
        return null;
      }

      if (contentLastModified.HasValue)
        File.SetLastWriteTimeUtc(tempFile, contentLastModified.Value.UtcDateTime);

      File.Move(tempFile, cachedFile, true);
      // Normalized so callers can compare it against a FileInfo.FullName (see the encoding-changed
      // cleanup in RevalidateAndServeAsync) without a raw-vs-normalized path-string mismatch.
      return Path.GetFullPath(cachedFile);
    }
    finally
    {
      // Covers every exit (success, content-length mismatch, a mid-stream client abort):
      // leave no orphaned .tmp file behind.
      CatchSilently(() =>
      {
        if (File.Exists(tempFile))
          File.Delete(tempFile);
      });
    }
  }

  // When we stored a cache file, read from its companion stamp's LastWriteTime; null when unstamped.
  // One extra stat, and only on paths whose rule sets a freshness window.
  //
  // The date deliberately lives beside the cache file rather than on it. The file's own LastWriteTime
  // is the upstream's Last-Modified (see DownloadToDiskAsync), which must stay put so the served
  // header and the ETag TypedResults.PhysicalFile derives from it do not churn on every revalidation.
  // Its creation time cannot be used either: on Linux .NET reports CreationTimeUtc as
  // min(btime, mtime) and has no way to write btime, so moving the stored date forward is silently
  // dropped (the window never restarts) while the upstream Last-Modified sitting in mtime drags the
  // reported creation time into the past (every entry born stale, revalidating on every request).
  private DateTime? GetStoredDate(string cacheFilePath)
  {
    var stamp = new FileInfo(CacheFileProvider.GetStoredDateStampPath(cacheFilePath));
    return stamp.Exists ? stamp.LastWriteTimeUtc : null;
  }

  // Records "stored now" for a cache file, restarting its freshness window. The stamp is empty: only
  // its LastWriteTime carries the date, so reading it back later costs a stat and never an open.
  private void TouchStoredDate(string cacheFilePath) =>
    CatchSilently(() =>
    {
      var stamp = CacheFileProvider.GetStoredDateStampPath(cacheFilePath);
      if (!File.Exists(stamp))
        File.Create(stamp).Dispose();
      File.SetLastWriteTimeUtc(stamp, myTimeProvider.GetUtcNow().UtcDateTime);
    });

  // A stamp is meaningless without its cache file, so the two are always dropped together.
  private void DeleteStoredDate(string cacheFilePath) =>
    CatchSilently(() =>
    {
      var stamp = CacheFileProvider.GetStoredDateStampPath(cacheFilePath);
      if (File.Exists(stamp)) File.Delete(stamp);
    });

  private void DeleteCachedVariants(Uri upstreamUri)
  {
    foreach (var encoding in new string?[] { null, "gzip" })
    {
      var path = Path.Combine(myLocalCachePath, upstreamUri.GetFutureCacheFileLocation(encoding));
      CatchSilently(() => { if (File.Exists(path)) File.Delete(path); });
      DeleteStoredDate(path);
    }
  }

  private void CatchSilently(Action action)
  {
    try
    {
      action();
    }
    catch (OperationCanceledException) {}
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
