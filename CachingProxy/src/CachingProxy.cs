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
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "UnusedMember.Global")]
public class CachingProxy
{
  private readonly RequestDelegate myRequestDelegate;
  private readonly ILogger<CachingProxy> myLogger;
  private readonly RemoteProxy myRemoteProxy;
  private readonly ResponseCache myResponseCache;
  private readonly TimeProvider myTimeProvider;
  private readonly string myLocalCachePath;

  private const int BUFFER_SIZE = 81920;

  public CachingProxy(
    RequestDelegate requestDelegate,
    ILogger<CachingProxy> logger,
    CachingProxyConfig config,
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

        // Everything needed to replay the stored bytes faithfully — the upstream's head and the date the
        // window is anchored to — comes from one companion file. Without it there is no media type we
        // could honestly serve the copy as (guessing one is what makes a registry client reject a
        // manifest outright), so treat the entry as absent and let the request refetch and rewrite it.
        if (ReadMetadata(cachedFile.FullName) is not { } metadata)
          break;

        // Past its freshness window: revalidate against the upstream before serving (200 replaces,
        // 304 keeps, 404 deletes, any other error serves the stale copy). With no window the stored
        // copy is fresh forever.
        if (rule?.RefreshAfter is { } refreshAfter && UtcNow - metadata.StoredAtUtc > refreshAfter)
        {
          await RevalidateAndServeAsync(context, remoteServer, upstreamUri, contentEncoding, cachedFile, metadata);
          return;
        }

        await ServeFileAsync(context, cachedFile, metadata, contentEncoding, CachingProxyStatus.HIT);
        return;
      }

      using var response = await myRemoteProxy.ProcessAsync(context, upstreamUri.ManglePath(),
        remoteServer.CacheDuration, upstreamUri, remoteServer.Auth, rule);

      // A non-null response is a GET MISS body for us to stream and persist; otherwise it is handled.
      if (response == null) return;

      var storedPath = await DownloadToDiskAsync(context, response, upstreamUri);
      if (storedPath == null) return;

      // Unconditional, unlike the freshness stamp this replaced: a stored copy needs its head back
      // whether or not its rule sets a window, and it is the same single write either way.
      WriteMetadata(storedPath, CacheEntryMetadata.FromResponse(UtcNow, response));
    }
    catch (OperationCanceledException)
    {
      // Probable cause: OperationCanceledException while streaming the upstream response body
      // Probable cause: OperationCanceledException from this service's client (context.RequestAborted)
      // However, if it was cancelled for any other reason, we need to prevent empty responses.
      context.Abort();
    }
  }

  // Replays the stored copy under the upstream's own head: its media type, its validators (so the
  // client's conditional requests are answered against the upstream's ETag and Last-Modified rather than
  // ones derived from the local file) and its Docker-Content-Digest, which an OCI client verifies the
  // body against. Content-Length and range handling stay with the file, which is what actually ships.
  private async Task ServeFileAsync(HttpContext context, FileInfo cachedFile, CacheEntryMetadata metadata,
    string? contentEncoding, CachingProxyStatus status)
  {
    myRemoteProxy.SetStatusHeader(context, status);
    CachedResponse.SetCachingHeaderFor(context);
    if (contentEncoding != null)
      context.Response.Headers.ContentEncoding = contentEncoding;
    if (metadata.Digest != null)
      context.Response.Headers[CachedResponse.DockerContentDigestHeader] = metadata.Digest;

    await TypedResults
      .PhysicalFile(cachedFile.FullName,
        // Octet-stream only for an upstream that sent no media type at all, matching what MISS emitted.
        metadata.ContentType ?? MediaTypeNames.Application.Octet,
        lastModified: metadata.LastModified,
        entityTag: ParseETag(metadata.ETag),
        enableRangeProcessing: true)
      .ExecuteAsync(context);
  }

  // Null for an upstream that sent no ETag, or one this framework will not parse — the served head then
  // simply carries no ETag, exactly as before it was recorded.
  private static EntityTagHeaderValue? ParseETag(string? etag) =>
    etag != null && EntityTagHeaderValue.TryParse(etag, out var parsed) ? parsed : null;

  private async Task RevalidateAndServeAsync(HttpContext context, RemoteServers.RemoteServer remoteServer,
    Uri upstreamUri, string? contentEncoding, FileInfo cachedFile, CacheEntryMetadata metadata)
  {
    // The conditional validators come from the entry's metadata. Last-Modified falls back to when we
    // stored the copy for an upstream that sent none: that is never earlier than the upstream's own, so
    // a resource changed since is still answered with a full body while an unchanged one still 304s.
    var result = await myRemoteProxy.RevalidateAsync(context, upstreamUri, metadata.ETag,
      metadata.LastModified ?? new DateTimeOffset(metadata.StoredAtUtc, TimeSpan.Zero),
      remoteServer.Auth, context.RequestAborted);

    switch (result.Outcome)
    {
      case RevalidationOutcome.NotModified:
        // Still valid: restart the window, keeping the head the stored bytes were fetched under.
        WriteMetadata(cachedFile.FullName, metadata with { StoredAtUtc = UtcNow });
        await ServeFileAsync(context, cachedFile, metadata, contentEncoding, CachingProxyStatus.REVALIDATED);
        return;

      case RevalidationOutcome.Gone:
        // Removed upstream: drop both encoding variants and cache+serve the 404, matching a MISS 404.
        // The 404 replaces any stored head under the same key, so nothing points at the deleted body.
        DeleteCachedVariants(upstreamUri);
        await myRemoteProxy.SetStatusAsync(context, CachingProxyStatus.NEGATIVE_MISS,
          await myResponseCache.PutStatusCode(upstreamUri.ManglePath(), HttpStatusCode.NotFound, remoteServer.CacheDuration, context.RequestAborted));
        return;

      case RevalidationOutcome.UpstreamError:
        // Could not reach/validate the upstream: keep and serve the stale copy.
        await ServeFileAsync(context, cachedFile, metadata, contentEncoding, CachingProxyStatus.STALE);
        return;

      case RevalidationOutcome.Replaced:
        using (var response = result.Response!)
        {
          // Write the (revalidated) response head, then stream+persist the new body. The head carries
          // the upstream's own Content-Type, which CachedResponse already copied over — the same one
          // recorded below, so this response and every later HIT agree.
          await myRemoteProxy.SetStatusAsync(context, CachingProxyStatus.REVALIDATED, new CachedResponse(response));

          var newPath = await DownloadToDiskAsync(context, response, upstreamUri);
          if (newPath == null) return;

          // The new representation may use a different encoding (hence a different path) than the
          // stale variant we matched; remove the now-orphaned old variant and its metadata.
          if (!string.Equals(newPath, cachedFile.FullName, StringComparison.Ordinal))
          {
            CatchSilently(() => { if (cachedFile.Exists) cachedFile.Delete(); });
            DeleteMetadata(cachedFile.FullName);
          }

          WriteMetadata(newPath, CacheEntryMetadata.FromResponse(UtcNow, response));
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

  private DateTime UtcNow => myTimeProvider.GetUtcNow().UtcDateTime;

  // What a stored copy needs to be replayed (see CacheEntryMetadata), or null when the companion file is
  // missing or unreadable — the caller then treats the entry as absent and refetches, which rewrites it.
  // One read, on the one path that has to hit the disk anyway.
  private static CacheEntryMetadata? ReadMetadata(string cacheFilePath)
  {
    try
    {
      return CacheEntryMetadata.TryParse(File.ReadAllText(CacheFileProvider.GetMetadataPath(cacheFilePath)));
    }
    catch (IOException) { return null; }
    catch (UnauthorizedAccessException) { return null; }
  }

  private void WriteMetadata(string cacheFilePath, CacheEntryMetadata metadata) =>
    CatchSilently(() => File.WriteAllText(CacheFileProvider.GetMetadataPath(cacheFilePath), metadata.Format()));

  // Metadata is meaningless without its cache file, so the two are always dropped together.
  private void DeleteMetadata(string cacheFilePath) =>
    CatchSilently(() =>
    {
      var path = CacheFileProvider.GetMetadataPath(cacheFilePath);
      if (File.Exists(path)) File.Delete(path);
    });

  private void DeleteCachedVariants(Uri upstreamUri)
  {
    foreach (var encoding in new string?[] { null, "gzip" })
    {
      var path = Path.Combine(myLocalCachePath, upstreamUri.GetFutureCacheFileLocation(encoding));
      CatchSilently(() => { if (File.Exists(path)) File.Delete(path); });
      DeleteMetadata(path);
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
