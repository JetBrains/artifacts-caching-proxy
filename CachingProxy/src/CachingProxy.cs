using System;
using System.Buffers;
using System.Collections.Generic;
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
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JetBrains.CachingProxy
{
  [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
  public class CachingProxy
  {
    private const int BUFFER_SIZE = 81920;

    private static readonly Regex ourGoodPathChars = new Regex("^[a-zA-Z_\\-0-9./]+$", RegexOptions.Compiled);

    private static readonly HttpClient ourHttpClient = new HttpClient
    {
      Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly Regex myBlacklistRegex;
    private readonly Regex myRedirectToRemoteUrlsRegex;
    private readonly ResponseCache myResponseCache = new ResponseCache();
    private readonly ILogger myLogger;
    private readonly FileExtensionContentTypeProvider myContentTypeProvider;
    private readonly RequestDelegate myNext;
    private readonly List<PathString> myPathStringPrefixes;
    private readonly StaticFileMiddleware myStaticFileMiddleware;

    private readonly CacheFileProvider myCacheFileProvider;

    public CachingProxy(RequestDelegate next, IHostingEnvironment hostingEnv,
      ILoggerFactory loggerFactory, IOptions<CachingProxyConfig> config)
    {
      myLogger = loggerFactory.CreateLogger(GetType().FullName);
      myLogger.LogWarning("Initialising. Config:\n" + config.Value);

      myNext = next;

      var localCachePath = config.Value.LocalCachePath;
      if (localCachePath == null) throw new ArgumentNullException("", "LocalCachePath could not be null");
      if (!Directory.Exists(localCachePath)) throw new ArgumentException("LocalCachePath doesn't exist: " + localCachePath);

      // Order by length here to handle longer prefixes first
      // This will help to handle overlapping prefixes like:
      // /aprefix
      // /aprefix/too
      var prefixes = config.Value.Prefixes.OrderByDescending(x => x.Length).ToList();
      myPathStringPrefixes = prefixes.Select(x => new PathString(x)).ToList();

      foreach (var prefix in prefixes)
      {
        var uri = UriFromRequestPath(prefix);
        // force reconnection (and DNS re-resolve) every two minutes
        ServicePointManager.FindServicePoint(uri).ConnectionLeaseTimeout = 120000;
      }

      myContentTypeProvider = new FileExtensionContentTypeProvider();
      myCacheFileProvider = new CacheFileProvider(localCachePath);

      var staticFileOptions = new StaticFileOptions
      {
        FileProvider = myCacheFileProvider,
        ServeUnknownFileTypes = true,
        ContentTypeProvider = myContentTypeProvider
      };

      myStaticFileMiddleware =
        new StaticFileMiddleware(next, hostingEnv, Options.Create(staticFileOptions), loggerFactory);

      myBlacklistRegex = config.Value.BlacklistUrlRegex != null
        ? new Regex(config.Value.BlacklistUrlRegex, RegexOptions.Compiled)
        : null;

      myRedirectToRemoteUrlsRegex = config.Value.RedirectToRemoteUrlsRegex != null
        ? new Regex(config.Value.RedirectToRemoteUrlsRegex, RegexOptions.Compiled)
        : null;
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    public async Task InvokeAsync(HttpContext context)
    {
      if (!myPathStringPrefixes.Any(x => context.Request.Path.StartsWithSegments(x)))
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

      var upstreamUri = UriFromRequestPath(requestPath);

      if (myBlacklistRegex != null && myBlacklistRegex.IsMatch(requestPath))
      {
        await SetStatus(context, CachingProxyStatus.BLACKLISTED, HttpStatusCode.NotFound, "Blacklisted");
        return;
      }

      if (myRedirectToRemoteUrlsRegex != null && myRedirectToRemoteUrlsRegex.IsMatch(requestPath))
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
        SetCachedResponseHeader(context, cachedResponse);
        await SetStatus(context, CachingProxyStatus.HIT, HttpStatusCode.OK);
        return;
      }

      myLogger.LogDebug("Downloading from {0}", upstreamUri);

      var request = new HttpRequestMessage(isHead ? HttpMethod.Head : HttpMethod.Get, upstreamUri);

      HttpResponseMessage response;
      try
      {
        response = await ourHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
      }
      catch (OperationCanceledException canceledException)
      {
        if (context.RequestAborted == canceledException.CancellationToken) return;

        // Canceled by internal token, means timeout

        myLogger.LogWarning($"Timeout requesting {upstreamUri}");

        var entry = myResponseCache.PutStatusCode(requestPath, HttpStatusCode.GatewayTimeout);

        SetCachedResponseHeader(context, entry);
        await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
        return;
      }
      catch (Exception e)
      {
        myLogger.LogWarning(e, $"Exception requesting {upstreamUri}: {e.Message}");

        var entry = myResponseCache.PutStatusCode(requestPath, HttpStatusCode.ServiceUnavailable);
        SetCachedResponseHeader(context, entry);
        await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
        return;
      }

      using (response)
      {
        if (!response.IsSuccessStatusCode)
        {
          var entry = myResponseCache.PutStatusCode(requestPath, response.StatusCode);

          SetCachedResponseHeader(context, entry);
          await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
          return;
        }

        if (response.IsSuccessStatusCode && isHead)
        {
          var entry = myResponseCache.PutStatusCode(requestPath, response.StatusCode);
          SetCachedResponseHeader(context, entry);
          await SetStatus(context, CachingProxyStatus.MISS, HttpStatusCode.OK);
          return;
        }

        var contentLength = response.Content.Headers.ContentLength;
        context.Response.ContentLength = contentLength;

        if (myContentTypeProvider.TryGetContentType(requestPath, out var contentType))
          context.Response.ContentType = contentType;

        await SetStatus(context, CachingProxyStatus.MISS, HttpStatusCode.OK);

        var tempFile = cachePath + ".tmp." + Guid.NewGuid();
        try
        {
          var parent = Directory.GetParent(cachePath);
          Directory.CreateDirectory(parent.FullName);

          using (var stream = new FileStream(
            tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, BUFFER_SIZE,
            FileOptions.Asynchronous))
          {
            using (var sourceStream = await response.Content.ReadAsStreamAsync())
              await CopyToTwoStreamsAsync(sourceStream, context.Response.Body, stream, context.RequestAborted);
          }

          var tempFileInfo = new FileInfo(tempFile);
          if (contentLength != null && tempFileInfo.Length != contentLength)
          {
            myLogger.LogWarning($"Expected {contentLength} bytes from Content-Length, but downloaded {tempFileInfo.Length}: {upstreamUri}");
            context.Abort();
            return;
          }

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

    private Uri UriFromRequestPath(string requestPath)
    {
      return new Uri("https://" + requestPath.TrimStart('/'));
    }

    private async Task SetStatus(HttpContext context, CachingProxyStatus status, HttpStatusCode? httpCode = null,
      string responseString = null)
    {
      context.Response.Headers[CachingProxyConstants.StatusHeader] = status.ToString();

      if (httpCode != null)
        context.Response.StatusCode = (int) httpCode;

      if (responseString != null)
        await context.Response.WriteAsync(responseString);
    }

    private void SetCachedResponseHeader(HttpContext context, ResponseCache.Entry entry)
    {
      context.Response.Headers[CachingProxyConstants.CachedStatusHeader] = ((int) entry.StatusCode).ToString();
      context.Response.Headers[CachingProxyConstants.CachedUntilHeader] = entry.CacheUntil.ToString("R");
    }

    private static async Task CopyToTwoStreamsAsync(Stream source, Stream dest1, Stream dest2,
      CancellationToken cancellationToken)
    {
      byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
      try
      {
        while (true)
        {
          int length = await source.ReadAsync(new Memory<byte>(buffer), cancellationToken).ConfigureAwait(false);
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
