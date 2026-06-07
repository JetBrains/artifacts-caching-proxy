using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
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
  [SuppressMessage("ReSharper", "UnusedMember.Global")]
  public partial class CachingProxy
  {
    private const int BUFFER_SIZE = 81920;

    [GeneratedRegex(@"^([\x20a-zA-Z_\-0-9./+@]|%[0-9a-fA-F]{2})+$", RegexOptions.Compiled)]
    private static partial Regex OurGoodPathChars { get; }

    private static readonly StringValues ourEternalCachingHeader =
      new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365) }.ToString();

    private readonly ILogger myLogger;
    private readonly RequestDelegate myNext;
    private readonly RemoteProxy myRemoteProxy;
    private readonly StaticFileMiddleware myStaticFileMiddleware;
    private readonly StaticFileOptions myStaticFileOptions;

    private readonly CacheFileProvider myCacheFileProvider;
    private readonly string myLocalCachePath;
    private readonly long myMinimumFreeDiskSpaceMb;

    public CachingProxy(RequestDelegate next, IWebHostEnvironment hostingEnv, ILoggerFactory loggerFactory,
      IOptions<CachingProxyConfig> config, RemoteProxy remoteProxy)
    {
      myLogger = loggerFactory.CreateLogger<CachingProxy>();
      myLogger.LogInformation("Initialising. Config:\n{CachingProxyConfig}", config.Value);

      myNext = next;
      myRemoteProxy = remoteProxy;

      myMinimumFreeDiskSpaceMb = config.Value.MinimumFreeDiskSpaceMb;
      myLocalCachePath = config.Value.LocalCachePath;
      if (string.IsNullOrEmpty(myLocalCachePath))
        throw new ArgumentNullException(nameof(myLocalCachePath), "LocalCachePath could not be null");
      if (!Directory.Exists(myLocalCachePath))
      {
        if (myLocalCachePath.StartsWith(Path.GetTempPath()))
          Directory.CreateDirectory(myLocalCachePath);
        else
          throw new ArgumentException("LocalCachePath doesn't exist: " + myLocalCachePath);
      }

      myCacheFileProvider = new CacheFileProvider(myLocalCachePath);

      myStaticFileOptions = new StaticFileOptions
      {
        FileProvider = myCacheFileProvider,
        ServeUnknownFileTypes = true,
        DefaultContentType = MediaTypeNames.Application.Octet,
        HttpsCompression = HttpsCompressionMode.DoNotCompress,
        ContentTypeProvider = new FileExtensionContentTypeProvider(),
        OnPrepareResponse = ctx =>
        {
          var contentEncoding = myCacheFileProvider.GetContentEncoding(ctx.File);
          if (contentEncoding != null)
            ctx.Context.Response.Headers.ContentEncoding = contentEncoding;

          myRemoteProxy.MarkStatus(ctx.Context, CachingProxyStatus.HIT);
          AddEternalCachingControl(ctx.Context);
        }
      };

      myStaticFileMiddleware =
        new StaticFileMiddleware(next, hostingEnv, Options.Create(myStaticFileOptions), loggerFactory);
    }

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

      var remoteServer = myRemoteProxy.LookupRemoteServer(context.Request.Path, out var remainingPath);
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

      var requestPath = context.Request.Path.Value ?? "";
      if (requestPath.Contains("..", StringComparison.Ordinal) || !OurGoodPathChars.IsMatch(requestPath))
      {
        myRemoteProxy.MarkStatus(context, CachingProxyStatus.BAD_REQUEST);
        context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
        await context.Response.WriteAsync("Invalid request path");
        return;
      }

      var contentType = myStaticFileOptions.ContentTypeProvider.TryGetContentType(requestPath, out var resolvedContentType)
        ? resolvedContentType
        : myStaticFileOptions.DefaultContentType;

      using var response = await myRemoteProxy.ProcessAsync(context, remoteServer, remainingPath, requestPath, isHead, contentType);

      // Every 200 OK we emit is a successfully resolved immutable artifact (HEAD cache hit/miss or
      // GET MISS body), so it may be cached forever; non-2xx outcomes (redirect, negative, errors)
      // are left uncached.
      if (context.Response.StatusCode == StatusCodes.Status200OK)
        AddEternalCachingControl(context);

      // A non-null response is a GET MISS body for us to stream and persist; otherwise it is handled.
      if (response == null) return;

      var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
      var contentLength = response.Content.Headers.ContentLength;
      var contentLastModified = response.Content.Headers.LastModified;

      var cachePath = myCacheFileProvider.GetFutureCacheFileLocation(requestPath, contentEncoding);
      if (cachePath == null)
      {
        myRemoteProxy.MarkStatus(context, CachingProxyStatus.BAD_REQUEST);
        context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
        await context.Response.WriteAsync("Invalid cache path");
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
          myLogger.LogWarning(Event.NotMatchedContentLength, "Expected {ContentLength} bytes from Content-Length, but downloaded {Length}: {RequestPath}",
            contentLength, tempFileInfo.Length, requestPath);
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

    private static void AddEternalCachingControl(HttpContext context)
    {
      context.Response.Headers.CacheControl = ourEternalCachingHeader;
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
