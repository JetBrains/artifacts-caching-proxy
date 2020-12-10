using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
using Microsoft.Net.Http.Headers;

namespace JetBrains.CachingProxy
{
  [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
  public class CachingProxy
  {
    private const int BUFFER_SIZE = 81920;

    private static readonly Regex ourGoodPathChars = new Regex("^([\\x20a-zA-Z_\\-0-9./]|%20)+$", RegexOptions.Compiled);
    private static readonly string ourEternalCachingHeader =
      new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365) }.ToString();

    private readonly Regex myBlacklistRegex;
    private readonly Regex myRedirectToRemoteUrlsRegex;
    private readonly ResponseCache myResponseCache = new ResponseCache();
    private readonly ILogger myLogger;
    private readonly FileExtensionContentTypeProvider myContentTypeProvider;
    private readonly RequestDelegate myNext;
    private readonly ProxyHttpClient myHttpClient;
    private readonly RemoteServers myRemoteServers;
    private readonly StaticFileMiddleware myStaticFileMiddleware;

    private readonly CacheFileProvider myCacheFileProvider;
    private readonly string myLocalCachePath;
    private readonly long myMinimumFreeDiskSpaceMb;

    public CachingProxy(RequestDelegate next, IWebHostEnvironment hostingEnv,
      ILoggerFactory loggerFactory, IOptions<CachingProxyConfig> config, ProxyHttpClient httpClient)
    {
      myLogger = loggerFactory.CreateLogger(GetType().FullName);
      myLogger.LogWarning("Initialising. Config:\n" + config.Value);

      myNext = next;
      myHttpClient = httpClient;

      myMinimumFreeDiskSpaceMb = config.Value.MinimumFreeDiskSpaceMb;
      myLocalCachePath = config.Value.LocalCachePath;
      if (myLocalCachePath == null) throw new ArgumentNullException("", "LocalCachePath could not be null");
      if (!Directory.Exists(myLocalCachePath)) throw new ArgumentException("LocalCachePath doesn't exist: " + myLocalCachePath);

      myRemoteServers = new RemoteServers(config.Value.Prefixes.ToList());
      foreach (var remoteServer in myRemoteServers.Servers)
      {
        // force reconnection (and DNS re-resolve) every two minutes
        ServicePointManager.FindServicePoint(remoteServer.RemoteUri).ConnectionLeaseTimeout = 120000;
      }

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

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    public async Task InvokeAsync(HttpContext context)
    {
      if (context.Request.Path == "/health")
      {
        var availableFreeSpaceMb = new DriveInfo(myLocalCachePath).AvailableFreeSpace / (1024 * 1024);
        if (availableFreeSpaceMb < myMinimumFreeDiskSpaceMb)
        {
          context.Response.StatusCode = 500;
          await context.Response.WriteAsync($"Not Enough Free Disk Space. {availableFreeSpaceMb} MB is free at {myLocalCachePath}, " +
                                            $"but minimum is {myMinimumFreeDiskSpaceMb} MB");
          return;
        }

        context.Response.StatusCode = 200;
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

      var isHead = context.Request.Method.Equals("HEAD", StringComparison.Ordinal);
      var isGet = context.Request.Method.Equals("GET", StringComparison.Ordinal);

      if (!isHead && !isGet) return;
      if (context.Response.StatusCode != StatusCodes.Status404NotFound) return;

      var requestPath = context.Request.Path.ToString().Replace('\\', '/').TrimStart('/');
      if (requestPath.Contains("..", StringComparison.Ordinal) ||
          !ourGoodPathChars.IsMatch(requestPath))
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
      var emptyFileExtension = Path.GetExtension(requestPath).Length == 0;
      if (isRedirectToRemoteUrl || emptyFileExtension)
      {
        await SetStatus(context, CachingProxyStatus.ALWAYS_REDIRECT, HttpStatusCode.TemporaryRedirect);
        context.Response.Headers.Add("Location", upstreamUri.ToString());
        return;
      }

      var cachePath = myCacheFileProvider.GetFileInfo(requestPath).PhysicalPath;
      if (cachePath == null)
      {
        await SetStatus(context, CachingProxyStatus.BAD_REQUEST, HttpStatusCode.BadRequest, "Invalid cache path");
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
        context.Response.Headers[HeaderNames.ContentType] = cachedResponse.ContentType;

        SetCachedResponseHeader(context, cachedResponse);
        await SetStatus(context, CachingProxyStatus.HIT, HttpStatusCode.OK);
        return;
      }

      myLogger.LogDebug("Downloading from {0}", upstreamUri);

      var request = new HttpRequestMessage(isHead ? HttpMethod.Head : HttpMethod.Get, upstreamUri);

      HttpResponseMessage response;
      try
      {
        response = await myHttpClient.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
      }
      catch (OperationCanceledException canceledException)
      {
        if (context.RequestAborted == canceledException.CancellationToken) return;

        // Canceled by internal token, means timeout

        myLogger.LogWarning($"Timeout requesting {upstreamUri}");

        var entry = myResponseCache.PutStatusCode(requestPath, HttpStatusCode.GatewayTimeout, lastModified: null, contentType: null, contentLength: null);

        SetCachedResponseHeader(context, entry);
        await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
        return;
      }
      catch (Exception e)
      {
        myLogger.LogWarning(e, $"Exception requesting {upstreamUri}: {e.Message}");

        var entry = myResponseCache.PutStatusCode(requestPath, HttpStatusCode.ServiceUnavailable, lastModified: null, contentType: null, contentLength: null);
        SetCachedResponseHeader(context, entry);
        await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
        return;
      }

      using (response)
      {
        if (!response.IsSuccessStatusCode)
        {
          var entry = myResponseCache.PutStatusCode(requestPath, response.StatusCode, lastModified: null, contentType: null, contentLength: null);

          SetCachedResponseHeader(context, entry);
          await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
          return;
        }

        var contentLength = response.Content.Headers.ContentLength;
        context.Response.ContentLength = contentLength;

        var contentLastModified = response.Content.Headers.LastModified;
        if (contentLastModified != null)
          context.Response.GetTypedHeaders().LastModified = contentLastModified;

        if (myContentTypeProvider.TryGetContentType(requestPath, out var contentType))
          context.Response.ContentType = contentType;

        if (isHead)
        {
          var entry = myResponseCache.PutStatusCode(
            requestPath, response.StatusCode,
            lastModified: contentLastModified, contentType: contentType, contentLength: contentLength);
          SetCachedResponseHeader(context, entry);
          await SetStatus(context, CachingProxyStatus.MISS, HttpStatusCode.OK);
          return;
        }

        await SetStatus(context, CachingProxyStatus.MISS, HttpStatusCode.OK);

        // Cache successful responses indefinitely
        // as we assume content won't be changed under a fixed url
        AddEternalCachingControl(context);

        var tempFile = cachePath + ".tmp." + Guid.NewGuid();
        try
        {
          var parent = Directory.GetParent(cachePath);
          Directory.CreateDirectory(parent.FullName);

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
            myLogger.LogWarning($"Expected {contentLength} bytes from Content-Length, but downloaded {tempFileInfo.Length}: {upstreamUri}");
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
              // It's ok, parallel request cached it before us
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
        myLogger.Log(LogLevel.Error, e, "LogSilently: " + e.Message);
      }
    }

    private static async Task SetStatus(HttpContext context, CachingProxyStatus status, HttpStatusCode? httpCode = null,
      string responseString = null)
    {
      SetStatusHeader(context, status);

      if (httpCode != null)
        context.Response.StatusCode = (int) httpCode;

      if (responseString != null)
        await context.Response.WriteAsync(responseString);
    }

    private static void SetStatusHeader(HttpContext context, CachingProxyStatus status)
    {
      context.Response.Headers[CachingProxyConstants.StatusHeader] = status.ToString();
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
      context.Response.Headers[HeaderNames.CacheControl] = ourEternalCachingHeader;
    }

    private static async Task CopyToTwoStreamsAsync(Stream source, Stream dest1, Stream dest2,
      CancellationToken cancellationToken)
    {
      var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
      try
      {
        while (true)
        {
          var length = await source.ReadAsync(new Memory<byte>(buffer), cancellationToken).ConfigureAwait(false);
          if (length == 0)
            break;

          await dest1.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, length), cancellationToken).ConfigureAwait(false);
          await dest2.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, length), cancellationToken).ConfigureAwait(false);
        }
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(buffer);
      }
    }
  }
}
