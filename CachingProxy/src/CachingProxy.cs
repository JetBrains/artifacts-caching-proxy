using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace JetBrains.CachingProxy
{
  public partial class CachingProxy
  {
    private const int BUFFER_SIZE = 81920;

    [GeneratedRegex(@"^([\x20a-zA-Z_\-0-9./+@]|%20)+$", RegexOptions.Compiled)]
    private static partial Regex OurGoodPathChars { get; }
    private static readonly StringValues ourEternalCachingHeader =
      new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365) }.ToString();

    private readonly Regex? myBlacklistRegex;
    private readonly Regex? myRedirectToRemoteUrlsRegex;
    private readonly ResponseCache myResponseCache;
    private readonly ILogger myLogger;
    private readonly FileExtensionContentTypeProvider myContentTypeProvider;
    private readonly RequestDelegate myNext;
    private readonly CachingProxyMetrics myMetrics;
    private readonly ProxyHttpClient myHttpClient;
    private readonly RemoteServers myRemoteServers;
    private readonly StaticFileMiddleware myStaticFileMiddleware;

    private readonly CacheFileProvider myCacheFileProvider;
    private readonly string myLocalCachePath;
    private readonly long myMinimumFreeDiskSpaceMb;

    public CachingProxy(RequestDelegate next, IWebHostEnvironment hostingEnv, CachingProxyMetrics metrics,
      ILoggerFactory loggerFactory, IOptions<CachingProxyConfig> config, ProxyHttpClient httpClient, ResponseCache responseCache)
    {
      myLogger = loggerFactory.CreateLogger<CachingProxy>();
      myLogger.LogInformation("Initialising. Config:\n{CachingProxyConfig}", config.Value);

      myNext = next;
      myMetrics = metrics;
      myHttpClient = httpClient;
      myResponseCache = responseCache;

      myMinimumFreeDiskSpaceMb = config.Value.MinimumFreeDiskSpaceMb;
      myLocalCachePath = config.Value.LocalCachePath;
      if (myLocalCachePath == null)
        throw new ArgumentNullException(nameof(myLocalCachePath), "LocalCachePath could not be null");
      if (!Directory.Exists(myLocalCachePath))
        throw new ArgumentException("LocalCachePath doesn't exist: " + myLocalCachePath);

      myRemoteServers = new RemoteServers(config.Value.Prefixes.ToList(), config.Value.ContentTypeValidationPrefixes.ToList());

      myContentTypeProvider = new FileExtensionContentTypeProvider();
      myCacheFileProvider = new CacheFileProvider(myLocalCachePath);

      var staticFileOptions = new StaticFileOptions
      {
        FileProvider = myCacheFileProvider,
        ServeUnknownFileTypes = true,
        HttpsCompression = HttpsCompressionMode.DoNotCompress,
        ContentTypeProvider = myContentTypeProvider,
        OnPrepareResponse = ctx =>
        {
          var contentEncoding = myCacheFileProvider.GetContentEncoding(ctx.File);
          if (contentEncoding != null)
            ctx.Context.Response.Headers.ContentEncoding = contentEncoding;

          SetStatusHeader(ctx.Context, CachingProxyStatus.HIT);
          AddEternalCachingControl(ctx.Context);
        }
      };

      myStaticFileMiddleware =
        new StaticFileMiddleware(next, hostingEnv, Options.Create(staticFileOptions), loggerFactory);

      myBlacklistRegex = !string.IsNullOrWhiteSpace(config.Value.BlacklistUrlRegex)
        ? new Regex(config.Value.BlacklistUrlRegex, RegexOptions.Compiled)
        : null;

      myRedirectToRemoteUrlsRegex = !string.IsNullOrWhiteSpace(config.Value.RedirectToRemoteUrlsRegex)
        ? new Regex(config.Value.RedirectToRemoteUrlsRegex, RegexOptions.Compiled)
        : null;
    }

    private static readonly FrozenSet<string> ourAllowedTextFileExtensions =
      FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        ".htm",
        ".html",
        ".txt",
        ".sha1",
        ".sha256",
        ".sha512",
        ".md5",
        ".module");

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    public async Task InvokeAsync(HttpContext context)
    {
      if (context.Request.Path == "/health")
      {
        var availableFreeSpaceMb = new DriveInfo(myLocalCachePath).AvailableFreeSpace / (1024 * 1024);
        if (availableFreeSpaceMb < myMinimumFreeDiskSpaceMb)
        {
          myLogger.LogError(Event.NotEnoughFreeDiskSpace,
            "Not Enough Free Disk Space. {AvailableFreeSpaceMb} MB is free at {LocalCachePath}, but minimum is {MinimumFreeDiskSpaceMb} MB",
            availableFreeSpaceMb, myLocalCachePath, myMinimumFreeDiskSpaceMb);
          context.Response.StatusCode = StatusCodes.Status500InternalServerError;
          await context.Response.WriteAsync(
            $"Not Enough Free Disk Space. {availableFreeSpaceMb} MB is free at {myLocalCachePath}, but minimum is {myMinimumFreeDiskSpaceMb} MB");
          return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync("OK");
        return;
      }

      var remoteServer = myRemoteServers.LookupRemoteServer(context.Request.Path, out var remainingPath);
      if (remoteServer == null)
      {
        await myNext(context);
        return;
      }

      await myStaticFileMiddleware.Invoke(context);

      var isHead = HttpMethods.IsHead(context.Request.Method);
      var isGet = HttpMethods.IsGet(context.Request.Method);

      if (!isHead && !isGet) return;
      if (context.Response.StatusCode != StatusCodes.Status404NotFound) return;

      var requestPath = context.Request.Path.ToString().Replace('\\', '/').TrimStart('/');
      if (requestPath.Contains("..", StringComparison.Ordinal) ||
          !OurGoodPathChars.IsMatch(requestPath))
      {
        await SetStatus(context, CachingProxyStatus.BAD_REQUEST, HttpStatusCode.BadRequest, "Invalid request path");
        return;
      }

      var upstreamUri = new Uri(remoteServer.RemoteUri, remainingPath.ToString().TrimStart('/'));

      if (myBlacklistRegex != null && myBlacklistRegex.IsMatch(requestPath))
      {
        await SetStatus(context, CachingProxyStatus.BLACKLISTED, HttpStatusCode.NotFound, "Blacklisted");
        return;
      }

      var isRedirectToRemoteUrl = myRedirectToRemoteUrlsRegex != null && myRedirectToRemoteUrlsRegex.IsMatch(requestPath);
      var requestPathExtension = Path.GetExtension(requestPath);
      var emptyFileExtension = requestPathExtension.Length == 0;
      if (isRedirectToRemoteUrl || emptyFileExtension)
      {
        await SetStatus(context, CachingProxyStatus.ALWAYS_REDIRECT, HttpStatusCode.TemporaryRedirect);
        context.Response.GetTypedHeaders().Location = upstreamUri;
        return;
      }

      var cachedResponse = myResponseCache.GetCachedStatusCode(requestPath);
      if (cachedResponse != null && !cachedResponse.StatusCode.IsSuccessStatusCode())
      {
        SetCachedResponseHeader(context, cachedResponse);
        await SetStatus(context, CachingProxyStatus.NEGATIVE_HIT, HttpStatusCode.NotFound);
        return;
      }

      // Positive caching for GET handled in static files
      // We handle positive caching for HEAD here
      if (cachedResponse != null && cachedResponse.StatusCode.IsSuccessStatusCode() && isHead)
      {
        var responseHeaders = context.Response.GetTypedHeaders();

        responseHeaders.LastModified = cachedResponse.LastModified;
        responseHeaders.ContentLength = cachedResponse.ContentLength;
        context.Response.Headers.ContentType = cachedResponse.ContentType;

        if (cachedResponse.ContentEncoding != null)
          context.Response.Headers.ContentEncoding = cachedResponse.ContentEncoding;

        SetCachedResponseHeader(context, cachedResponse);
        await SetStatus(context, CachingProxyStatus.HIT, HttpStatusCode.OK);
        return;
      }

      myLogger.LogDebug("Downloading from {UpstreamUri}", upstreamUri);

      var request = new HttpRequestMessage(isHead ? HttpMethod.Head : HttpMethod.Get, upstreamUri);

      HttpResponseMessage response;
      try
      {
        response = await myHttpClient.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
      }
      catch (OperationCanceledException canceledException)
      {
        if (context.RequestAborted == canceledException.CancellationToken) return;

        // Canceled by internal token means timeout

        myLogger.LogWarning(Event.Timeout, "Timeout requesting {UpstreamUri}", upstreamUri);

        var entry = myResponseCache.PutStatusCode(requestPath, HttpStatusCode.GatewayTimeout, lastModified: null, contentType: null, contentEncoding: null, contentLength: null);

        SetCachedResponseHeader(context, entry);
        await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
        return;
      }
      catch (Exception e)
      {
        myLogger.LogWarning(e, "Exception requesting {UpstreamUri}: {Message}", upstreamUri, e.Message);

        var entry = myResponseCache.PutStatusCode(requestPath, HttpStatusCode.ServiceUnavailable, lastModified: null, contentType: null, contentEncoding: null, contentLength: null);
        SetCachedResponseHeader(context, entry);
        await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
        return;
      }

      using (response)
      {
        if (!response.IsSuccessStatusCode)
        {
          if (response.StatusCode != HttpStatusCode.NotFound)
          {
            myLogger.LogWarning(Event.NegativeMiss(response.StatusCode), "Non-success requesting {UpstreamUri}: {StatusCode}", upstreamUri, response.StatusCode);
          }

          var entry = myResponseCache.PutStatusCode(requestPath, response.StatusCode, lastModified: null, contentType: null, contentEncoding: null, contentLength: null);

          SetCachedResponseHeader(context, entry);
          await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
          return;
        }

        // If content type validation is enabled, only specified files may have text/* content type
        // This prevents, e.g., caching of error pages with 200 OK code (jcenter)
        var responseContentType = response.Content.Headers.ContentType?.MediaType;
        if (!ourAllowedTextFileExtensions.Contains(requestPathExtension))
        {
          if (responseContentType is MediaTypeNames.Text.Html or MediaTypeNames.Text.Plain)
          {
            myLogger.Log(remoteServer.ValidateContentTypes ? LogLevel.Error : LogLevel.Warning, Event.NotAllowedContentType,
              "{UpstreamUri} returned content type '{ResponseContentType}' which is possibly wrong for file extension '{RequestPathExtension}'",
              upstreamUri, responseContentType, requestPathExtension);

            if (remoteServer.ValidateContentTypes)
            {
              // return 503 Service Unavailable, since the client will most likely not retry it with 5xx error codes
              context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
              context.Response.ContentType = MediaTypeNames.Text.Plain;
              await context.Response.WriteAsync(
                $"{upstreamUri} returned content type '{responseContentType}' which is forbidden by content type validation for file extension '{requestPathExtension}'");
              return;
            }
          }
        }

        var contentLength = response.Content.Headers.ContentLength;
        context.Response.ContentLength = contentLength;

        var contentLastModified = response.Content.Headers.LastModified;
        if (contentLastModified != null)
          context.Response.GetTypedHeaders().LastModified = contentLastModified;

        var headersContentEncoding = response.Content.Headers.ContentEncoding;
        if (headersContentEncoding.Count > 1)
        {
          myLogger.LogError(Event.MultipleContentTypes, "{UpstreamUri} returned multiple Content-Encoding which is not allowed: {ContentEncoding}",
            upstreamUri, string.Join(", ", headersContentEncoding));
          // return 503 Service Unavailable, since the client will most likely not retry it with 5xx error codes
          context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
          context.Response.ContentType = MediaTypeNames.Text.Plain;
          await context.Response.WriteAsync(
            $"{upstreamUri} returned multiple Content-Encoding which is not allowed: {string.Join(", ", headersContentEncoding)}");
          return;
        }

        var contentEncoding = headersContentEncoding.Count == 0 ? null : headersContentEncoding.Single();
        if (contentEncoding != null && contentEncoding != "gzip")
        {
          myLogger.LogError(Event.NotSupportedContentType, "{UpstreamUri} returned Content-Encoding '{ContentEncoding}' which is not supported",
            upstreamUri, contentEncoding);
          // return 503 Service Unavailable, since the client will most likely not retry it with 5xx error codes
          context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
          context.Response.ContentType = MediaTypeNames.Text.Plain;
          await context.Response.WriteAsync(
            $"{upstreamUri} returned Content-Encoding '{contentEncoding}' which is not supported");
          return;
        }

        if (contentEncoding != null)
          context.Response.Headers.ContentEncoding = contentEncoding;

        if (myContentTypeProvider.TryGetContentType(requestPath, out var contentType))
          context.Response.ContentType = contentType;

        if (isHead)
        {
          var entry = myResponseCache.PutStatusCode(
            requestPath, response.StatusCode,
            lastModified: contentLastModified, contentType: contentType, contentEncoding: contentEncoding, contentLength: contentLength);
          SetCachedResponseHeader(context, entry);
          await SetStatus(context, CachingProxyStatus.MISS, HttpStatusCode.OK);
          return;
        }

        await SetStatus(context, CachingProxyStatus.MISS, HttpStatusCode.OK);

        // Cache successful responses indefinitely
        // as we assume content won't be changed under a fixed url
        AddEternalCachingControl(context);

        var cachePath = myCacheFileProvider.GetFutureCacheFileLocation(requestPath, contentEncoding);
        if (cachePath == null)
        {
          await SetStatus(context, CachingProxyStatus.BAD_REQUEST, HttpStatusCode.BadRequest, "Invalid cache path");
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
            myLogger.LogWarning(Event.NotMatchedContentLength, "Expected {ContentLength} bytes from Content-Length, but downloaded {Length}: {UpstreamUri}",
              contentLength, tempFileInfo.Length, upstreamUri);
            context.Abort();
            return;
          }

          if (contentLastModified.HasValue) File.SetLastWriteTimeUtc(tempFile, contentLastModified.Value.UtcDateTime);

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
          // Probable cause: OperationCanceledException from http client myHttpClient
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

    private async Task SetStatus(HttpContext context, CachingProxyStatus status, HttpStatusCode? httpCode = null, string? responseString = null)
    {
      SetStatusHeader(context, status);

      if (httpCode != null)
        context.Response.StatusCode = (int) httpCode;

      if (responseString != null)
        await context.Response.WriteAsync(responseString);
    }

    private void SetStatusHeader(HttpContext context, CachingProxyStatus status)
    {
      context.Response.Headers[CachingProxyConstants.StatusHeader] = status.ToString();
      myMetrics.IncrementRequests(status);
    }

    private static void SetCachedResponseHeader(HttpContext context, ResponseCache.Entry entry)
    {
      context.Response.Headers[CachingProxyConstants.CachedStatusHeader] = ((int) entry.StatusCode).ToString();
      context.Response.Headers[CachingProxyConstants.CachedUntilHeader] = entry.CacheUntil.ToString("R");

      // Cache successful responses indefinitely
      // as we assume content won't be changed under a fixed url
      if (entry.StatusCode.IsSuccessStatusCode())
        AddEternalCachingControl(context);
    }

    private static void AddEternalCachingControl(HttpContext context)
    {
      context.Response.Headers.CacheControl = ourEternalCachingHeader;
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
  }
}
