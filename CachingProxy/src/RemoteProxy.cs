using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy;

/// <summary>
/// Remote upstream layer: resolves the remote server for a request, applies redirect/blacklist
/// rules, serves from and stores into the in-memory <see cref="ResponseCache"/>, performs the
/// upstream HTTP request and validates its response. It has no knowledge of local (disk) storage:
/// for a successful GET it hands the open upstream response back to the caller so the body can be
/// streamed and persisted elsewhere.
/// </summary>
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public partial class RemoteProxy(
  CachingProxyConfig config,
  ProxyHttpClient httpClient,
  ResponseCache responseCache,
  CachingProxyMetrics metrics,
  ILogger<RemoteProxy> logger)
{
  [GeneratedRegex(@"^([\x20a-zA-Z_\-0-9./+@]|%[0-9a-fA-F]{2})+$", RegexOptions.Compiled)]
  private static partial Regex OurGoodPathChars { get; }

  private readonly Regex? myBlacklistRegex = !string.IsNullOrWhiteSpace(config.BlacklistUrlRegex) ?
    new Regex(config.BlacklistUrlRegex, RegexOptions.Compiled) : null;

  private readonly Regex? myRedirectToRemoteUrlsRegex = !string.IsNullOrWhiteSpace(config.RedirectToRemoteUrlsRegex) ?
    new Regex(config.RedirectToRemoteUrlsRegex, RegexOptions.Compiled) : null;

  private readonly RemoteServers myRemoteServers = new([.. config.Prefixes]);

  public RemoteServers.RemoteServer? LookupRemoteServer(PathString url, out PathString remainingPart) =>
    myRemoteServers.LookupRemoteServer(url, out remainingPart);

  /// <summary>
  /// Validates the request method (only GET/HEAD are allowed) and path (no traversal, only safe
  /// characters). On failure it writes the appropriate response (405 or 400 BAD_REQUEST) and
  /// returns <c>false</c>; otherwise returns <c>true</c>. Both this layer and storage middlewares
  /// (disk, S3) call it before doing any upstream/storage work so the checks are applied uniformly.
  /// </summary>
  public async ValueTask<bool> ValidateRequestAsync(HttpContext context)
  {
    if (!HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsGet(context.Request.Method))
    {
      await SetStatus(context, CachingProxyStatus.BAD_REQUEST, HttpStatusCode.MethodNotAllowed);
      return false;
    }

    var requestPath = context.Request.Path.Value!;
    if (requestPath.Contains("..", StringComparison.Ordinal) || !OurGoodPathChars.IsMatch(requestPath))
    {
      await SetStatus(context, CachingProxyStatus.BAD_REQUEST, HttpStatusCode.BadRequest, "Invalid request path");
      return false;
    }

    return true;
  }

  /// <summary>
  /// Processes a request against the given remote server: handles redirects, in-memory cache
  /// hits, the upstream request and its validation (including Content-Encoding). Writes the full
  /// response head — status, metadata headers (Content-Length, Last-Modified), representation
  /// headers (Content-Type, Content-Encoding) and the proxy bookkeeping headers — to
  /// <paramref name="context"/>. The <paramref name="getContentType"/> is supplied by the caller
  /// (this layer could derive it): it is applied to the response and stored in the in-memory
  /// cache so later HEAD cache hits report the same type. For a successful GET the open
  /// upstream response is returned so the caller can stream and persist the body (reading its
  /// Content-Encoding off the response) and must dispose it; in every other case the request is
  /// fully handled, the response (if any) is disposed internally, and <c>null</c> is returned.
  /// </summary>
  public async Task<HttpResponseMessage?> ProcessAsync(HttpContext context, RemoteServers.RemoteServer remoteServer, PathString remainingPath,
    Func<HttpResponseMessage, string?>? getContentType = null)
  {
    if (!await ValidateRequestAsync(context)) return null;

    var isHead = HttpMethods.IsHead(context.Request.Method);
    var requestPath = context.Request.Path.Value!;

    var cachedResponse = responseCache.GetCachedStatusCode(requestPath);
    switch (cachedResponse?.StatusCode)
    {
      case >= HttpStatusCode.BadRequest:
        SetStatus(context, CachingProxyStatus.NEGATIVE_HIT, HttpStatusCode.NotFound, cachedResponse.Headers);
        return null;

      // Cached redirects (3xx) replay for any method. Positive 2xx replays only for HEAD: GET
      // populates the on-disk cache and is served by the static-file middleware, while a HEAD has
      // no body to persist so its positive result is cached in memory here instead. (A HEAD whose
      // file already exists on disk from a prior GET is served earlier by the static-file
      // middleware and never reaches this layer.)
      case >= HttpStatusCode.MultipleChoices:
      case >= HttpStatusCode.OK when isHead:
        SetStatus(context, CachingProxyStatus.HIT, cachedResponse);
        return null;
    }

    if (myBlacklistRegex != null && myBlacklistRegex.IsMatch(requestPath))
    {
      await SetStatus(context, CachingProxyStatus.BLACKLISTED, HttpStatusCode.NotFound, "Blacklisted");
      return null;
    }

    var upstreamUri = remoteServer.RemoteUri;
    if (remainingPath != PathString.Empty)
    {
      upstreamUri = new Uri(upstreamUri, remainingPath.Value.AsSpan(remainingPath.Value?[0] == '/' ? 1 : 0).ToString());
    }

    var isRedirectToRemoteUrl = myRedirectToRemoteUrlsRegex != null && myRedirectToRemoteUrlsRegex.IsMatch(requestPath);
    if (isRedirectToRemoteUrl)
    {
      IHeaderDictionary redirectHeaders = new HeaderDictionary();
      redirectHeaders.Location = upstreamUri.ToString();
      cachedResponse = new CachedResponse(HttpStatusCode.RedirectKeepVerb, redirectHeaders);
      SetStatus(context, CachingProxyStatus.ALWAYS_REDIRECT, cachedResponse);
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
      SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound, entry.Headers);
      return null;
    }
    catch (Exception e)
    {
      logger.LogWarning(e, "Exception requesting {UpstreamUri}: {Message}", upstreamUri, e.Message);

      var entry = responseCache.PutStatusCode(requestPath, HttpStatusCode.ServiceUnavailable, remoteServer.CacheDuration);
      SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound, entry.Headers);
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
        SetStatus(context, CachingProxyStatus.NEGATIVE_MISS, HttpStatusCode.NotFound, entry.Headers);
        return null;
      }

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

      IHeaderDictionary headers = new HeaderDictionary();
      headers.LastModified = response.Content.Headers.LastModified?.ToString("R");
      headers.ContentLength = response.Content.Headers.ContentLength;
      headers.ContentType = getContentType?.Invoke(response) ?? response.Content.Headers.ContentType?.MediaType;
      headers.ContentEncoding = contentEncoding;
      headers.CacheControl = response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices ? OurEternalCachingHeader : default;

      var responseEntry = new CachedResponse(response.StatusCode, headers);

      if (isHead)
      {
        responseCache.PutStatusCode(requestPath, remoteServer.CacheDuration, responseEntry);
        SetStatus(context, CachingProxyStatus.MISS, responseEntry);
        return null;
      }

      SetStatus(context, CachingProxyStatus.MISS, responseEntry);
      transferOwnership = true;
      return response;
    }
    finally
    {
      if (!transferOwnership) response.Dispose();
    }
  }


  public static readonly StringValues OurEternalCachingHeader =
    new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365) }.ToString();

  public async ValueTask SetStatus(HttpContext context, CachingProxyStatus status, HttpStatusCode? httpCode = null, string? responseString = null)
  {
    SetStatusHeader(context, status);

    if (httpCode != null)
      context.Response.StatusCode = (int) httpCode;

    if (responseString != null)
      await context.Response.WriteAsync(responseString);
  }

  public void SetStatus(HttpContext context, CachingProxyStatus status, CachedResponse response) =>
    SetStatus(context, status, response.StatusCode, response.Headers);

  private void SetStatus(HttpContext context, CachingProxyStatus status, HttpStatusCode statusCode, IHeaderDictionary headers)
  {
    SetStatusHeader(context, status);
    context.Response.StatusCode = (int)statusCode;
    SetCachedResponseHeader(context, headers);
  }

  public void SetStatusHeader(HttpContext context, CachingProxyStatus status)
  {
    context.Response.Headers[CachingProxyConstants.StatusHeader] = status.ToString();
    metrics.IncrementRequests(status);
  }

  private static void SetCachedResponseHeader(HttpContext context, IHeaderDictionary headers)
  {
    foreach (var (key, value) in headers)
    {
      context.Response.Headers[key] = value;
    }
  }
}
