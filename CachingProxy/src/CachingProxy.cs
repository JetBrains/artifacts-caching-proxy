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
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JetBrains.CachingProxy
{
  [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
  public class CachingProxy
  {
    private const int BUFFER_SIZE = 81920;
    private static readonly HttpClient ourHttpClient = new HttpClient {Timeout = TimeSpan.FromSeconds(10)};

    private readonly RequestDelegate myNext;
    private readonly CachingProxyConfig myConfig;
    private readonly List<PathString> myPathStringPrefixes;
    private readonly StaticFileMiddleware myStaticFileMiddleware;

    public CachingProxy(RequestDelegate next, IHostingEnvironment hostingEnv,
      ILoggerFactory loggerFactory, CachingProxyConfig config)
    {
      myNext = next;
      myConfig = config;

      // Order by length here to handle longer prefixes first
      // This will help to handle overlapping prefixes like:
      // /aprefix
      // /aprefix/too
      var prefixes = config.Prefixes.OrderByDescending(x => x.Length).ToList();
      myPathStringPrefixes = prefixes.Select(x => new PathString(x)).ToList();

      foreach (var prefix in prefixes)
      {
        // TODO?
        var uri = new Uri("https:/" + prefix);
        // force reconnection (and DNS re-resolve) every two minutes
        ServicePointManager.FindServicePoint(uri).ConnectionLeaseTimeout = 120000;
      }

      var staticFileOptions = new StaticFileOptions
      {
        FileProvider = new PhysicalFileProvider(myConfig.LocalCachePath),
      };

      myStaticFileMiddleware =
        new StaticFileMiddleware(next, hostingEnv, Options.Create(staticFileOptions), loggerFactory);

      myBlacklistRegex = config.BlacklistUrlRegex != null
        ? new Regex(config.BlacklistUrlRegex, RegexOptions.Compiled)
        : null;

      myRedirectToRemoteUrlsRegex = config.RedirectToRemoteUrlsRegex != null
        ? new Regex(config.RedirectToRemoteUrlsRegex, RegexOptions.Compiled)
        : null;

      Console.Error.WriteLine("CachingProxy init");
    }

    private static readonly Regex ourGoodPathChars = new Regex("^[a-zA-Z_\\-0-9./]+$", RegexOptions.Compiled);
    private readonly Regex myBlacklistRegex;
    private readonly Regex myRedirectToRemoteUrlsRegex;
    private readonly ResponseCache myResponseCache = new ResponseCache();

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    public async Task InvokeAsync(HttpContext context)
    {
      if (!myPathStringPrefixes.Any(x => context.Request.Path.StartsWithSegments(x)))
      {
        await myNext(context);
        return;
      }

      await myStaticFileMiddleware.Invoke(context);

      if (!context.Request.Method.Equals("GET")) return;
      if (context.Response.StatusCode != StatusCodes.Status404NotFound) return;

      var requestPath = context.Request.Path.ToString().Replace('\\', '/').TrimStart('/');
      if (requestPath.Contains("..", StringComparison.Ordinal) ||
          !ourGoodPathChars.IsMatch(requestPath))
      {
        await SetStatus(context, CachingProxyStatus.BAD_REQUEST, HttpStatusCode.BadRequest, "Invalid request path");
        return;
      }

      // TODO?
      var upstreamUri = new Uri("https://" + requestPath);

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

      var localCachePath = myConfig.LocalCachePath;
      if (localCachePath == null) throw new Exception("Null LocalCachePath");

      var cachePath = Path.GetFullPath(Path.Combine(localCachePath, requestPath));
      if (cachePath != localCachePath && !cachePath.StartsWith(localCachePath + Path.DirectorySeparatorChar))
      {
        await SetStatus(context, CachingProxyStatus.BAD_REQUEST, HttpStatusCode.BadRequest, "Path traversal");
        return;
      }

      var cachedResponse = myResponseCache.GetCachedStatusCode(requestPath);
      if (cachedResponse != null)
      {
        SetCachedResponseHeader(context, cachedResponse);
        await SetStatus(context, CachingProxyStatus.NEGATIVE_HIT, HttpStatusCode.NotFound);
        return;
      }

      Console.Error.WriteLine("---- 0 downloading from " + upstreamUri);

      var request = new HttpRequestMessage(HttpMethod.Get, upstreamUri);
      using (var response = await ourHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
      {
        Console.Error.WriteLine($"---- 2 {response.StatusCode}");
        if (!response.IsSuccessStatusCode)
        {
          var entry = myResponseCache.PutStatusCode(requestPath, response.StatusCode);
          
          SetCachedResponseHeader(context, entry);
          await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
          return;
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength == null)
        {
          await SetStatus(context, CachingProxyStatus.ERROR, HttpStatusCode.InternalServerError,
            "Remote server has not returned Content-Length header: " + upstreamUri);
          return;
        }

        context.Response.ContentLength = contentLength;

        await SetStatus(context, CachingProxyStatus.MISS, HttpStatusCode.OK);
        
        var tempFile = cachePath + ".tmp." + Guid.NewGuid().ToString();
        try
        {
          var parent = Directory.GetParent(cachePath);
          Directory.CreateDirectory(parent.FullName);

          using (var stream = new FileStream(
            tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, BUFFER_SIZE,
            FileOptions.Asynchronous))
          {
            using (var sourceStream = await response.Content.ReadAsStreamAsync())
            {
              // TODO Handle context.RequestAborted
              // Stopping download from a client should stop downloading and streaming here
              await CopyToTwoStreamsAsync(sourceStream, context.Response.Body, stream,
                default(CancellationToken));
            }
          }

          var tempFileInfo = new FileInfo(tempFile);
          if (tempFileInfo.Length != contentLength)
            throw new Exception(
              $"Expected {contentLength} bytes from Content-Length, but downloaded {tempFileInfo.Length}: {upstreamUri}");

          // TODO test, will report exception on overwrite, we need to skip it
          File.Move(tempFile, cachePath);
        }
        finally
        {
          try
          {
            if (File.Exists(tempFile))
              File.Delete(tempFile);
          }
          catch
          {
            // TODO. LogSilently?
          }
        }
      }
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
      context.Response.Headers[CachingProxyConstants.CachedStatusHeader] = ((int)entry.StatusCode).ToString();
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