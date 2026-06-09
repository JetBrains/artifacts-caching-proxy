using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace JetBrains.CachingProxy;

/// <summary>
/// Remote upstream layer: resolves the remote server for a request, applies redirect/blacklist
/// rules, serves from and stores into the in-memory <see cref="ResponseCache"/>, performs the
/// upstream HTTP request and validates its response. It has no knowledge of local (disk) storage:
/// for a successful GET it hands the open upstream response back to the caller so the body can be
/// streamed and persisted elsewhere.
/// </summary>
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public class RemoteProxy(
  CachingProxyConfig config,
  ProxyHttpClient httpClient,
  ResponseCache responseCache,
  CachingProxyMetrics metrics,
  TimeProvider timeProvider,
  ILogger<RemoteProxy> logger)
{
  private readonly Regex? myBlacklistRegex = !string.IsNullOrWhiteSpace(config.BlacklistUrlRegex) ?
    new Regex(config.BlacklistUrlRegex, RegexOptions.Compiled) : null;

  private readonly Regex? myRedirectToRemoteUrlsRegex = !string.IsNullOrWhiteSpace(config.RedirectToRemoteUrlsRegex) ?
    new Regex(config.RedirectToRemoteUrlsRegex, RegexOptions.Compiled) : null;

  private readonly RemoteServers myRemoteServers = new([.. config.Prefixes]);

  public RemoteServers.RemoteServer? LookupRemoteServer(PathString url, out PathString remainingPart) =>
    myRemoteServers.LookupRemoteServer(url, out remainingPart);

  /// <summary>
  /// Processes a request against the given remote server: handles redirects, in-memory cache
  /// hits, the upstream request and its validation (including Content-Encoding). Writes the full
  /// response head — status, metadata headers (Content-Length, Last-Modified), representation
  /// headers (Content-Type, Content-Encoding) and the proxy bookkeeping headers — to
  /// <paramref name="context"/>. The <paramref name="contentType"/> is supplied by the caller
  /// (this layer does not derive it): it is applied to the response and stored in the in-memory
  /// cache so later HEAD cache hits report the same type. The <paramref name="requestPath"/> is
  /// assumed to be already validated and safe by the caller. For a successful GET the open
  /// upstream response is returned so the caller can stream and persist the body (reading its
  /// Content-Encoding off the response) and must dispose it; in every other case the request is
  /// fully handled, the response (if any) is disposed internally, and <c>null</c> is returned.
  /// </summary>
  public async Task<HttpResponseMessage?> ProcessAsync(HttpContext context, RemoteServers.RemoteServer remoteServer,
    PathString remainingPath, string requestPath, bool isHead, string? contentType)
  {
    var upstreamUri = remoteServer.RemoteUri;
    if (remainingPath != PathString.Empty)
    {
      upstreamUri = new Uri(upstreamUri, remainingPath.Value.AsSpan(remainingPath.Value?[0] == '/' ? 1 : 0).ToString());
    }

    if (myBlacklistRegex != null && myBlacklistRegex.IsMatch(requestPath))
    {
      await SetStatus(context, CachingProxyStatus.BLACKLISTED, HttpStatusCode.NotFound, "Blacklisted");
      return null;
    }

    var isRedirectToRemoteUrl = myRedirectToRemoteUrlsRegex != null && myRedirectToRemoteUrlsRegex.IsMatch(requestPath);
    if (isRedirectToRemoteUrl)
    {
      await SetStatus(context, CachingProxyStatus.ALWAYS_REDIRECT, HttpStatusCode.RedirectKeepVerb);
      context.Response.GetTypedHeaders().Location = upstreamUri;
      return null;
    }

    var cachedResponse = responseCache.GetCachedStatusCode(requestPath);
    if (cachedResponse != null && !cachedResponse.StatusCode.IsSuccessStatusCode())
    {
      SetCachedResponseHeader(context, cachedResponse);
      await SetStatus(context, CachingProxyStatus.NEGATIVE_HIT, HttpStatusCode.NotFound);
      return null;
    }

    // GET populates the on-disk cache, so a repeated GET is served by the static-file middleware.
    // A HEAD has no body to persist, so its positive result is cached in memory here instead.
    // (A HEAD whose file already exists on disk from a prior GET is served earlier by the
    // static-file middleware and never reaches this layer.)
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
      return null;
    }

    logger.LogDebug("Downloading from {UpstreamUri}", upstreamUri);

    var request = new HttpRequestMessage(isHead ? HttpMethod.Head : HttpMethod.Get, upstreamUri);

    HttpResponseMessage response;
    try
    {
      response = await httpClient.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    }
    catch (OperationCanceledException canceledException)
    {
      if (context.RequestAborted == canceledException.CancellationToken) return null;

      // Canceled by internal token means timeout

      logger.LogWarning(Event.Timeout, "Timeout requesting {UpstreamUri}", upstreamUri);

      var entry = responseCache.PutStatusCode(requestPath, HttpStatusCode.GatewayTimeout, remoteServer.CacheDuration);

      SetCachedResponseHeader(context, entry);
      await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
      return null;
    }
    catch (Exception e)
    {
      logger.LogWarning(e, "Exception requesting {UpstreamUri}: {Message}", upstreamUri, e.Message);

      var entry = responseCache.PutStatusCode(requestPath, HttpStatusCode.ServiceUnavailable, remoteServer.CacheDuration);
      SetCachedResponseHeader(context, entry);
      await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
      return null;
    }

    var transferOwnership = false;
    try
    {
      if (!response.IsSuccessStatusCode)
      {
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
          logger.LogWarning(Event.NegativeMiss(response.StatusCode), "Non-success requesting {UpstreamUri}: {StatusCode}", upstreamUri, response.StatusCode);
        }

        var entry = responseCache.PutStatusCode(requestPath, response.StatusCode, remoteServer.CacheDuration);

        SetCachedResponseHeader(context, entry);
        await SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound);
        return null;
      }

      var contentLength = response.Content.Headers.ContentLength;
      context.Response.ContentLength = contentLength;

      var contentLastModified = response.Content.Headers.LastModified;
      if (contentLastModified != null)
        context.Response.GetTypedHeaders().LastModified = contentLastModified;

      var headersContentEncoding = response.Content.Headers.ContentEncoding;
      if (headersContentEncoding.Count > 1)
      {
        logger.LogError(Event.MultipleContentTypes, "{UpstreamUri} returned multiple Content-Encoding which is not allowed: {ContentEncoding}",
          upstreamUri, string.Join(", ", headersContentEncoding));
        // return 503 Service Unavailable, since the client will most likely not retry it with 5xx error codes
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = MediaTypeNames.Text.Plain;
        await context.Response.WriteAsync(
          $"{upstreamUri} returned multiple Content-Encoding which is not allowed: {string.Join(", ", headersContentEncoding)}");
        return null;
      }

      var contentEncoding = headersContentEncoding.Count == 0 ? null : headersContentEncoding.Single();
      if (contentEncoding != null && contentEncoding != "gzip")
      {
        logger.LogError(Event.NotSupportedContentType, "{UpstreamUri} returned Content-Encoding '{ContentEncoding}' which is not supported",
          upstreamUri, contentEncoding);
        // return 503 Service Unavailable, since the client will most likely not retry it with 5xx error codes
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = MediaTypeNames.Text.Plain;
        await context.Response.WriteAsync(
          $"{upstreamUri} returned Content-Encoding '{contentEncoding}' which is not supported");
        return null;
      }

      if (contentEncoding != null)
        context.Response.Headers.ContentEncoding = contentEncoding;

      if (contentType != null)
        context.Response.ContentType = contentType;

      if (isHead)
      {
        var entry = responseCache.PutStatusCode(
          requestPath, response.StatusCode, remoteServer.CacheDuration,
          lastModified: contentLastModified, contentType: contentType, contentEncoding: contentEncoding, contentLength: contentLength);
        SetCachedResponseHeader(context, entry);
        await SetStatus(context, CachingProxyStatus.MISS, HttpStatusCode.OK);
        return null;
      }

      await SetStatus(context, CachingProxyStatus.MISS, HttpStatusCode.OK);

      transferOwnership = true;
      return response;
    }
    finally
    {
      if (!transferOwnership) response.Dispose();
    }
  }

  public void MarkStatus(HttpContext context, CachingProxyStatus status) => SetStatusHeader(context, status);

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
    metrics.IncrementRequests(status);
  }

  private void SetCachedResponseHeader(HttpContext context, ResponseCache.Entry entry)
  {
    context.Response.Headers[CachingProxyConstants.CachedStatusHeader] = ((int) entry.StatusCode).ToString();
    context.Response.Headers[CachingProxyConstants.CachedUntilHeader] = (timeProvider.GetUtcNow() + entry.GetCacheTimeSpan()) .ToString("R");
  }

  private static readonly StringValues ourEternalCachingHeader =
    new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365) }.ToString();

  public void AddEternalCachingControl(HttpContext context) =>
    context.Response.Headers.CacheControl = ourEternalCachingHeader;
}
