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
  private readonly TimeSpan myIdleReadTimeout;

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
    myIdleReadTimeout = config.IdleReadTimeoutSec > 0
      ? TimeSpan.FromSeconds(config.IdleReadTimeoutSec)
      : Timeout.InfiniteTimeSpan;
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

    // A content-negotiated endpoint keeps one entry per requested representation, so the variant has to
    // reach every key derived below: the in-memory key and the cache file name (see GetCacheVariant).
    var variant = RemoteProxy.GetCacheVariant(context, rule);
    var cacheKey = upstreamUri.ManglePath(variant);

    try
    {
      foreach (var contentEncoding in GetCacheLookupContentEncodings(context))
      {
        var cachedFile = new FileInfo(Path.Combine(myLocalCachePath, upstreamUri.GetFutureCacheFileLocation(contentEncoding, variant)));
        if (!cachedFile.Exists)
          continue;

        // Everything needed to replay the stored bytes faithfully — the upstream's head and the date the
        // window is anchored to — comes from one companion file. Without it there is no media type we
        // could honestly serve the copy as (guessing one is what makes a registry client reject a
        // manifest outright), so treat the entry as absent and let the request refetch and rewrite it.
        if (await ReadMetadataAsync(cachedFile.FullName, context.RequestAborted) is not { } metadata)
          break;

        // Past its freshness window: revalidate against the upstream before serving (200 replaces,
        // 304 keeps, 404 deletes, any other error serves the stale copy). With no window the stored
        // copy is fresh forever.
        if (rule?.RefreshAfter is { } refreshAfter && UtcNow - metadata.StoredAtUtc > refreshAfter)
        {
          await RevalidateAndServeAsync(context, remoteServer, upstreamUri, cacheKey, contentEncoding, cachedFile, metadata, rule, variant);
          return;
        }

        await ServeFileAsync(context, cachedFile, metadata, contentEncoding, CachingProxyStatus.HIT);
        return;
      }

      using var response = await myRemoteProxy.ProcessAsync(context, cacheKey,
        remoteServer.CacheDuration, upstreamUri, remoteServer.Auth, rule, remoteServer.Profile);

      // A non-null response is a GET MISS body for us to stream and persist; otherwise it is handled.
      if (response == null) return;

      var storedPath = await DownloadToDiskAsync(context, response, upstreamUri, variant, CachingProxyStatus.MISS);
      if (storedPath == null) return;

      // Unconditional, unlike the freshness stamp this replaced: a stored copy needs its head back
      // whether or not its rule sets a window, and it is the same single write either way.
      await WriteMetadataAsync(storedPath, CacheEntryMetadata.FromResponse(UtcNow, response));
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
    myRemoteProxy.SetStatusHeader(context, status, cachedContentLength: null);
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

    // What went out, not what is on disk: the result above answers conditional and ranged requests itself,
    // so a client that already holds the artifact gets a bodiless 304 and a resumed download gets one range
    // of it. Both are common enough on a repository (Gradle revalidates, layer pulls resume) that counting
    // the file every time would report traffic that never left. The framework leaves the length it served
    // on the response, and a 304 leaves none.
    if (context.Response.ContentLength is { } served)
      myRemoteProxy.AddContentBytes(context, status, served);
  }

  // Null for an upstream that sent no ETag, or one this framework will not parse — the served head then
  // simply carries no ETag, exactly as before it was recorded.
  private static EntityTagHeaderValue? ParseETag(string? etag) =>
    etag != null && EntityTagHeaderValue.TryParse(etag, out var parsed) ? parsed : null;

  private async Task RevalidateAndServeAsync(HttpContext context, RemoteServers.RemoteServer remoteServer,
    Uri upstreamUri, string cacheKey, string? contentEncoding, FileInfo cachedFile,
    CacheEntryMetadata metadata, CachingRule? rule, string? variant)
  {
    // The conditional validators come from the entry's metadata. Last-Modified falls back to when we
    // stored the copy for an upstream that sent none: that is never earlier than the upstream's own, so
    // a resource changed since is still answered with a full body while an unchanged one still 304s.
    var result = await myRemoteProxy.RevalidateAsync(context, upstreamUri, metadata.ETag,
      metadata.LastModified ?? new DateTimeOffset(metadata.StoredAtUtc, TimeSpan.Zero),
      remoteServer.Auth, rule, remoteServer.Profile, context.RequestAborted);

    switch (result.Outcome)
    {
      case RevalidationOutcome.NotModified:
        // Still valid: restart the window, keeping the head the stored bytes were fetched under.
        await WriteMetadataAsync(cachedFile.FullName, metadata with { StoredAtUtc = UtcNow });
        await ServeFileAsync(context, cachedFile, metadata, contentEncoding, CachingProxyStatus.REVALIDATED);
        return;

      case RevalidationOutcome.Gone:
        // Removed upstream: drop both encoding variants and cache+serve the 404, matching a MISS 404.
        // The 404 replaces any stored head under the same key, so nothing points at the deleted body.
        DeleteCachedVariants(upstreamUri, variant);
        await myRemoteProxy.SetStatusAsync(context, CachingProxyStatus.NEGATIVE_MISS,
          await myResponseCache.PutStatusCode(cacheKey, HttpStatusCode.NotFound, remoteServer.CacheDuration, context.RequestAborted, rule?.RefreshAfter));
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
          // recorded below, so this response and every later HIT agree. Its bytes are counted by the
          // transfer that relays them, like a MISS's.
          await myRemoteProxy.SetStatusAsync(context, CachingProxyStatus.REVALIDATED, new CachedResponse(response),
            countContentBytes: false);

          var newPath = await DownloadToDiskAsync(context, response, upstreamUri, variant, CachingProxyStatus.REVALIDATED);
          if (newPath == null) return;

          // The new representation may use a different encoding (hence a different path) than the
          // stale variant we matched; remove the now-orphaned old variant and its metadata.
          if (!string.Equals(newPath, cachedFile.FullName, StringComparison.Ordinal))
          {
            CatchSilently(() => { if (cachedFile.Exists) cachedFile.Delete(); });
            DeleteMetadata(cachedFile.FullName);
          }

          await WriteMetadataAsync(newPath, CacheEntryMetadata.FromResponse(UtcNow, response));
        }
        return;
    }
  }

  // Streams the upstream body to the client while persisting it to the local cache via an atomic
  // temp-file + move, validating Content-Length. Returns the final cache file path, or null when the
  // download was aborted (content-length mismatch, a stalled upstream, or cancellation). Leaves no
  // orphaned temp file. Reports the bytes it relayed to the client under the given status, which is the
  // status the head for this body was written with.
  private async Task<string?> DownloadToDiskAsync(HttpContext context, HttpResponseMessage response, Uri upstreamUri,
    string? variant, CachingProxyStatus status)
  {
    var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
    var contentLength = response.Content.Headers.ContentLength;
    var contentLastModified = response.Content.Headers.LastModified;

    var cachedFile = Path.Combine(myLocalCachePath, upstreamUri.GetFutureCacheFileLocation(contentEncoding, variant));
    var tempFile = cachedFile + ".tmp." + Guid.NewGuid();

    var parent = Directory.GetParent(cachedFile);
    Directory.CreateDirectory(parent!.FullName);

    try
    {
      long writtenLength;
      await using (var stream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, BUFFER_SIZE, FileOptions.Asynchronous))
      {
        await using (var sourceStream = await response.Content.ReadAsStreamAsync(context.RequestAborted))
          writtenLength = await CopyToTwoStreamsAsync(sourceStream, context.Response.Body, stream, context.RequestAborted);
      }

      // Counted while copying rather than stat'ed off the finished file: the copy knows the number
      // already, and a stat has no async form to keep this path off the blocking APIs.
      if (contentLength != null && writtenLength != contentLength)
      {
        myLogger.LogWarning(Event.NotMatchedContentLength, "Expected {ContentLength} bytes from Content-Length, but downloaded {Length}: {RequestPath}",
          contentLength, writtenLength, context.Request.Path);
        context.Abort();
        return null;
      }

      if (contentLastModified.HasValue)
        File.SetLastWriteTimeUtc(tempFile, contentLastModified.Value.UtcDateTime);

      // A concurrent request for the same artifact downloads to its own temp file and then moves it onto
      // this same destination. On Windows that collides: the replace cannot delete a destination another
      // handle still holds open, whether that handle is the other mover or a reader already serving it.
      // Both racers fetched the same upstream bytes and the cache is last-writer-wins, so the copy that
      // is already there is as good as ours. Failing here would abort a response whose body this method
      // has by now streamed to the client in full, turning cache bookkeeping into a client-visible error.
      try
      {
        File.Move(tempFile, cachedFile, true);
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException)
      {
        // Only a lost race leaves the destination in place. Anything else - a bad path, no permission on
        // the cache directory - has to keep surfacing rather than be silently downgraded to a cache hit.
        if (!File.Exists(cachedFile)) throw;

        myLogger.Log(LogLevel.Debug, Event.ConcurrentCacheStore, e,
          "Another request stored {RequestPath} first, keeping its copy", context.Request.Path);
      }

      // The copy wrote the same bytes to the client and to the cache, so this is what the request served -
      // counted here and not off the head, which carries the upstream's declared length and, for a chunked
      // upstream, no length at all. An aborted download returns above and counts nothing.
      myRemoteProxy.AddContentBytes(context, status, writtenLength);

      // Normalized so callers can compare it against a FileInfo.FullName (see the encoding-changed
      // cleanup in RevalidateAndServeAsync) without a raw-vs-normalized path-string mismatch.
      return Path.GetFullPath(cachedFile);
    }
    catch (IncompleteUpstreamBodyException e)
    {
      // The upstream went silent or died mid-body (see CopyToTwoStreamsAsync). Handled exactly like a
      // Content-Length mismatch, and for the same reason: the head - status and all - was written and
      // partially drained long ago, so there is no error response left to send, and the only honest signal
      // is to abort so the client sees a truncated transfer instead of a body short of its Content-Length.
      // Returning null keeps the partial temp file out of the cache (the finally below deletes it), and
      // unlike a head-phase timeout nothing is negative-cached: the next request must retry, not 404.
      myLogger.LogWarning(Event.IncompleteUpstreamBody, "Aborting {RequestPath}: {Reason}", context.Request.Path, e.Message);
      context.Abort();
      return null;
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
  private static async Task<CacheEntryMetadata?> ReadMetadataAsync(string cacheFilePath, CancellationToken cancellationToken)
  {
    try
    {
      return CacheEntryMetadata.TryParse(
        await File.ReadAllTextAsync(CacheFileProvider.GetMetadataPath(cacheFilePath), cancellationToken));
    }
    catch (IOException) { return null; }
    catch (UnauthorizedAccessException) { return null; }
  }

  // Deliberately not cancellable: by the time this runs the bytes are already on disk, and a cache file
  // whose companion never got written reads as absent, so honouring a client disconnect here would trade
  // a completed store for a guaranteed refetch.
  private Task WriteMetadataAsync(string cacheFilePath, CacheEntryMetadata metadata) =>
    CatchSilentlyAsync(() => File.WriteAllTextAsync(CacheFileProvider.GetMetadataPath(cacheFilePath), metadata.Format()));

  // Metadata is meaningless without its cache file, so the two are always dropped together.
  private void DeleteMetadata(string cacheFilePath) =>
    CatchSilently(() =>
    {
      var path = CacheFileProvider.GetMetadataPath(cacheFilePath);
      if (File.Exists(path)) File.Delete(path);
    });

  // Both content-encoding variants of one entry. Not the Accept variants: those are separate entries under
  // their own keys, and each revalidates (or 404s) on its own request.
  private void DeleteCachedVariants(Uri upstreamUri, string? variant)
  {
    foreach (var encoding in new string?[] { null, "gzip" })
    {
      var path = Path.Combine(myLocalCachePath, upstreamUri.GetFutureCacheFileLocation(encoding, variant));
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

  private async Task CatchSilentlyAsync(Func<Task> action)
  {
    try
    {
      await action();
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

  // Returns the number of bytes copied, so the caller can validate Content-Length without stat'ing.
  //
  // Enforces CachingProxyConfig.IdleReadTimeoutSec on the upstream reads: the timer is armed for each read
  // and disarmed once bytes arrive, so it measures silence rather than total duration and a gigabyte-scale
  // layer transfers for as long as it keeps making progress. This is the only bound on a body transfer -
  // see the config comment for why HttpClient.Timeout is not one.
  //
  // Both ways the upstream can fail to deliver the body - going silent, or ending it early - surface as
  // IncompleteUpstreamBodyException, so the caller can tell them from the OperationCanceledException a
  // departing client raises and from an IOException its own disk writes raise. The writes stay on the
  // caller's token: a client too slow to drain is Kestrel's to time out (MinResponseDataRate), and counting
  // that as an upstream stall would blame the wrong side.
  private async Task<long> CopyToTwoStreamsAsync(Stream source, Stream dest1, FileStream dest2, CancellationToken cancellationToken)
  {
    using var buffer = MemoryPool<byte>.Shared.Rent(BUFFER_SIZE);
    var memory = buffer.Memory;
    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    long total = 0;
    while (true)
    {
      int length;
      idleCts.CancelAfter(myIdleReadTimeout);
      try
      {
        length = await source.ReadAsync(memory, idleCts.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        // Our own token fired while the caller's did not, so this is the idle timer and not a disconnect.
        throw new IncompleteUpstreamBodyException(
          $"Upstream sent no data for {myIdleReadTimeout.TotalSeconds:0}s after {total} bytes");
      }
      catch (IOException e)
      {
        // The upstream ended the body short of its Content-Length, or its connection died. No timer catches
        // this - the read fails rather than hangs - so it gets the stall's treatment. Caught around the read
        // alone: the same type comes out of our own cache-file writes, where it means a bad disk and must surface.
        throw new IncompleteUpstreamBodyException($"Upstream ended the body after {total} bytes: {e.Message}", e);
      }
      finally
      {
        idleCts.CancelAfter(Timeout.InfiniteTimeSpan);
      }

      if (length == 0)
        break;

      await dest1.WriteAsync(memory[..length], cancellationToken).ConfigureAwait(false);
      await dest2.WriteAsync(memory[..length], cancellationToken).ConfigureAwait(false);
      total += length;
    }

    return total;
  }

  // The upstream did not deliver the body it promised: it went silent, or it ended early. One type because
  // the two get one treatment, and its own type so the caller can tell them from a disk failure.
  private sealed class IncompleteUpstreamBodyException(string message, Exception? innerException = null)
    : Exception(message, innerException);

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
