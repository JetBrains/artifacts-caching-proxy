using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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
  ILogger<RemoteProxy> logger,
  IUpstreamAuthorizationProvider? authProvider = null)
{
  [GeneratedRegex(@"^([\x20a-zA-Z_\-0-9./+@]|%[0-9a-fA-F]{2})+$", RegexOptions.Compiled)]
  private static partial Regex OurGoodPathChars { get; }

  private readonly Regex? myBlacklistRegex = !string.IsNullOrWhiteSpace(config.BlacklistUrlRegex) ?
    new Regex(config.BlacklistUrlRegex, RegexOptions.Compiled) : null;

  /// <summary>
  /// Validates the request method (only GET/HEAD are allowed), the path (no traversal, only safe
  /// characters) and that the path resolves to a well-formed upstream target. On a failure it writes
  /// the appropriate response (405 or 400 BAD_REQUEST) and returns <c>false</c>; otherwise returns
  /// <c>true</c>. Both this layer and storage middlewares (disk, S3) call it before doing any
  /// upstream/storage work so the checks are applied uniformly.
  /// </summary>
  /// <returns>Upstream URI</returns>
  public async ValueTask<Uri?> ValidateRequestAsync(HttpContext context, RemoteServers.RemoteServer remoteServer, string? remainingPath)
  {
    if (!HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsGet(context.Request.Method))
    {
      await SetStatusAsync(context, CachingProxyStatus.BAD_REQUEST, CachedResponse.MethodNotAllowed);
      return null;
    }

    var requestPath = context.Request.Path.Value!;
    if (requestPath.Contains("..", StringComparison.Ordinal) || !OurGoodPathChars.IsMatch(requestPath))
    {
      await SetStatusAsync(context, CachingProxyStatus.BAD_REQUEST, CachedResponse.InvalidPath);
      return null;
    }

    // The remainder after the prefix is resolved against the upstream base via new Uri(base, ...).
    // A remainder with a leading "//" (e.g. "/<prefix>////-.jar") is an RFC-3986 network-path
    // reference that resolves to an empty/foreign authority and throws here; reject it as a bad
    // request rather than letting it surface downstream.
    try
    {
      return remoteServer.GetUpstreamUri(remainingPath);
    }
    catch (UriFormatException)
    {
      await SetStatusAsync(context, CachingProxyStatus.BAD_REQUEST, CachedResponse.InvalidPath);
      return null;
    }
  }

  // 404 and authentication / access errors are surfaced to the client verbatim (we do not mask
  // them); every other non-success status is masked to 404. Used for both negative cache hits and
  // misses so a replayed entry returns the same status the live response did.
  private static HttpStatusCode ClientFacingStatus(HttpStatusCode upstream) => upstream switch
  {
    HttpStatusCode.NotFound or
      HttpStatusCode.Unauthorized or
      HttpStatusCode.PaymentRequired or
      HttpStatusCode.Forbidden or
      HttpStatusCode.ProxyAuthenticationRequired or
      HttpStatusCode.UnavailableForLegalReasons => upstream,
    _ => HttpStatusCode.NotFound,
  };

  /// <summary>
  /// Processes a request against the given remote server: handles redirects, in-memory cache
  /// hits, the upstream request and its validation (including Content-Encoding). Writes the full
  /// response head — status, metadata headers (Content-Length, Last-Modified), representation
  /// headers (Content-Type, Content-Encoding) and the proxy bookkeeping headers — to
  /// <paramref name="context"/>. The Content-Type is the one returned by <paramref name="contentType"/>
  /// when the caller supplies a resolver (the disk backend resolves it from the file extension, so a
  /// MISS and a later HIT served from disk agree), and the upstream's own Content-Type otherwise. It
  /// is stored in the in-memory cache so later HEAD hits agree. For a successful GET the open
  /// upstream response is returned so the caller can stream and persist the body (reading its
  /// Content-Encoding off the response) and must dispose it; in every other case the request is
  /// fully handled, the response (if any) is disposed internally, and <c>null</c> is returned.
  /// </summary>
  public async Task<HttpResponseMessage?> ProcessAsync(HttpContext context, string cacheKey, CacheDuration cacheDuration, Uri upstreamUri, string? contentType = null, UpstreamAuth? auth = null, CachingRule? rule = null)
  {
    var isHead = HttpMethods.IsHead(context.Request.Method);

    var cachedResponse = await responseCache.GetCachedStatusCode(cacheKey, context.RequestAborted);
    switch (cachedResponse?.StatusCode)
    {
      case >= HttpStatusCode.BadRequest:
        await SetStatusAsync(context, CachingProxyStatus.NEGATIVE_HIT,
          cachedResponse with { StatusCode = ClientFacingStatus(cachedResponse.StatusCode) });
        return null;

      // The caller decides whether the key includes the HTTP method (the S3 backend does so to keep a
      // large object's HEAD metadata distinct from its GET redirect), so such a redirect only ever
      // replays for the same verb it was stored under. The redirect Location itself is verb-agnostic
      // (the S3 backend signs it on the fly per request). A cached 2xx is replayed when it carries the
      // full body (the S3 backend inlines small objects into the cache) or when the request is a HEAD
      // (which needs only the metadata); a bodyless 2xx is never replayed to a GET, whose body lives
      // on disk/S3 instead.
      case >= HttpStatusCode.MultipleChoices:
      case >= HttpStatusCode.OK when isHead || cachedResponse.Body != null:
        await SetStatusAsync(context, CachingProxyStatus.HIT, cachedResponse);
        return null;
    }

    var requestPath = context.Request.Path.Value!;
    if (myBlacklistRegex != null && myBlacklistRegex.IsMatch(requestPath))
    {
      await SetStatusAsync(context, CachingProxyStatus.BLACKLISTED, CachedResponse.Blacklisted);
      return null;
    }

    // A caching profile rule may mark a path as always-redirected: it is bounced to the origin with
    // 307 (RedirectKeepVerb) instead of being cached. This holds for authenticated upstreams too: a
    // 307 preserves the method and the client reuses its own credentials for the origin, so there is
    // no need to proxy these through (which would wrongly cache dynamic/non-cacheable content for
    // protected sources). With no matching rule the path is cached, never redirected.
    if (rule is { Redirect: true })
    {
      await SetStatusAsync(context, CachingProxyStatus.ALWAYS_REDIRECT,
        new CachedResponse(HttpStatusCode.RedirectKeepVerb, new HeaderDictionary())
        {
          Headers = { Location = upstreamUri.ToString() }
        });
      return null;
    }

    logger.LogDebug("Downloading from {UpstreamUri}", upstreamUri);

    var request = new HttpRequestMessage(isHead ? HttpMethod.Head : HttpMethod.Get, upstreamUri);

    HttpResponseMessage response;
    try
    {
      if (authProvider != null && auth != null)
      {
        request.Headers.Authorization = await authProvider.GetAuthorizationHeaderAsync(auth, context.RequestAborted);
      }
      response = await httpClient.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    }
    catch (OperationCanceledException canceledException)
    {
      if (canceledException.CancellationToken == context.RequestAborted) throw;

      // Canceled by internal token means timeout

      logger.LogWarning(Event.Timeout, "Timeout requesting {UpstreamUri}", upstreamUri);

      var entry = await responseCache.PutStatusCode(cacheKey, HttpStatusCode.GatewayTimeout, cacheDuration, context.RequestAborted);
      await SetStatusAsync(context, CachingProxyStatus.NEGATIVE_MISS, entry with { StatusCode = HttpStatusCode.NotFound });
      return null;
    }
    catch (InvalidOperationException e)
    {
      return await InternalServerError(context, Event.RemoteProxy, "Remote proxy error", e);
    }
    catch (Exception e)
    {
      logger.LogWarning(e, "Exception requesting {UpstreamUri}: {Message}", upstreamUri, e.Message);

      var entry = await responseCache.PutStatusCode(cacheKey, HttpStatusCode.ServiceUnavailable, cacheDuration, context.RequestAborted);
      await SetStatusAsync(context, CachingProxyStatus.NEGATIVE_MISS, entry with { StatusCode = HttpStatusCode.NotFound });
      return null;
    }

    var transferOwnership = false;
    try
    {
      if (!response.IsSuccessStatusCode)
      {
        var entry = await responseCache.PutStatusCode(cacheKey, response.StatusCode, cacheDuration, context.RequestAborted);
        if (ClientFacingStatus(response.StatusCode) is var statusCode && statusCode != response.StatusCode)
        {
          entry = entry with { StatusCode = statusCode };
        }
        switch (statusCode)
        {
          case HttpStatusCode.NotFound when statusCode != response.StatusCode:
          case not HttpStatusCode.NotFound when auth != null:
            logger.LogWarning(Event.NegativeMiss(response.StatusCode),
              "Non-success requesting {UpstreamUri}: {StatusCode}", upstreamUri, response.StatusCode);
            break;
        }

        await SetStatusAsync(context, CachingProxyStatus.NEGATIVE_MISS, entry);
        return null;
      }

      switch (ValidateContentEncoding(response, out var contentEncoding))
      {
        case ContentEncodingValidation.Multiple:
          return await InternalServerError(context, Event.MultipleContentTypes,
            $"{upstreamUri} returned multiple Content-Encoding which is not allowed: {string.Join(", ", response.Content.Headers.ContentEncoding)}");
        case ContentEncodingValidation.Unsupported:
          return await InternalServerError(context, Event.NotSupportedContentType,
            $"{upstreamUri} returned Content-Encoding '{contentEncoding}' which is not supported");
      }

      var responseEntry = new CachedResponse(response)
      {
        Headers =
        {
          ContentType = contentType ?? response.Content.Headers.ContentType?.ToString(),
        }
      };

      if (isHead)
      {
        await SetStatusAsync(context, CachingProxyStatus.MISS,
          await responseCache.PutStatusCode(cacheKey, responseEntry, cacheDuration, context.RequestAborted));
        return null;
      }

      await SetStatusAsync(context, CachingProxyStatus.MISS, responseEntry);
      transferOwnership = true;
      return response;
    }
    finally
    {
      if (!transferOwnership) response.Dispose();
    }
  }


  /// <summary>
  /// Revalidates a stale stored copy against the upstream with a conditional GET. Unlike
  /// <see cref="ProcessAsync"/>, this never writes a negative cache entry and never touches the
  /// response on <paramref name="context"/>; the caller decides what to serve from the outcome:
  /// <list type="bullet">
  /// <item><c>304 Not Modified</c> → <see cref="RevalidationOutcome.NotModified"/> (keep the stored copy).</item>
  /// <item><c>404</c> → <see cref="RevalidationOutcome.Gone"/> (delete the stored copy).</item>
  /// <item>a <c>2xx</c> with a storable Content-Encoding → <see cref="RevalidationOutcome.Replaced"/>;
  /// the open response is returned for the caller to stream/store and <b>must be disposed</b>.</item>
  /// <item>anything else (other status, timeout, network error, unsupported encoding) →
  /// <see cref="RevalidationOutcome.UpstreamError"/> (serve the stale copy as-is).</item>
  /// </list>
  /// </summary>
  public async Task<RevalidationResult> RevalidateAsync(HttpContext context, Uri upstreamUri,
    string? etag, DateTimeOffset? lastModified, UpstreamAuth? auth, CancellationToken cancellationToken)
  {
    logger.LogDebug("Revalidating {UpstreamUri}", upstreamUri);

    var request = new HttpRequestMessage(HttpMethod.Get, upstreamUri);
    if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var parsedEtag))
      request.Headers.IfNoneMatch.Add(parsedEtag);
    if (lastModified.HasValue)
      request.Headers.IfModifiedSince = lastModified.Value;

    HttpResponseMessage response;
    try
    {
      if (authProvider != null && auth != null)
        request.Headers.Authorization = await authProvider.GetAuthorizationHeaderAsync(auth, cancellationToken);
      response = await httpClient.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
    catch (OperationCanceledException canceledException)
    {
      if (canceledException.CancellationToken == context.RequestAborted) throw;
      // Canceled by the internal token means a timeout: keep and serve the stale copy.
      logger.LogWarning(Event.Timeout, "Timeout revalidating {UpstreamUri}; serving stale", upstreamUri);
      return new RevalidationResult(RevalidationOutcome.UpstreamError);
    }
    catch (Exception e)
    {
      logger.LogWarning(e, "Exception revalidating {UpstreamUri}: {Message}; serving stale", upstreamUri, e.Message);
      return new RevalidationResult(RevalidationOutcome.UpstreamError);
    }

    var transferOwnership = false;
    try
    {
      switch (response.StatusCode)
      {
        case HttpStatusCode.NotModified:
          return new RevalidationResult(RevalidationOutcome.NotModified);
        case HttpStatusCode.NotFound:
          return new RevalidationResult(RevalidationOutcome.Gone);
      }

      if (!response.IsSuccessStatusCode)
      {
        logger.LogWarning(Event.NegativeMiss(response.StatusCode),
          "Non-success revalidating {UpstreamUri}: {StatusCode}; serving stale", upstreamUri, response.StatusCode);
        return new RevalidationResult(RevalidationOutcome.UpstreamError);
      }

      if (ValidateContentEncoding(response, out var contentEncoding) != ContentEncodingValidation.Ok)
      {
        logger.LogWarning(Event.NotSupportedContentType,
          "Unsupported Content-Encoding '{ContentEncoding}' revalidating {UpstreamUri}; serving stale", contentEncoding, upstreamUri);
        return new RevalidationResult(RevalidationOutcome.UpstreamError);
      }

      transferOwnership = true;
      return new RevalidationResult(RevalidationOutcome.Replaced, response);
    }
    finally
    {
      if (!transferOwnership) response.Dispose();
    }
  }

  private enum ContentEncodingValidation { Ok, Multiple, Unsupported }

  // Only an absent or a single "gzip" Content-Encoding is storable (the disk and S3 backends keep
  // just plain and gzip variants). Reports the normalized encoding (null = none) via the out param.
  private static ContentEncodingValidation ValidateContentEncoding(HttpResponseMessage response, out string? contentEncoding)
  {
    var headers = response.Content.Headers.ContentEncoding;
    if (headers.Count > 1)
    {
      contentEncoding = null;
      return ContentEncodingValidation.Multiple;
    }

    contentEncoding = headers.Count == 0 ? null : headers.Single();
    return contentEncoding is null or "gzip" ? ContentEncodingValidation.Ok : ContentEncodingValidation.Unsupported;
  }

  public async ValueTask SetStatusAsync(HttpContext context, CachingProxyStatus status, CachedResponse response)
  {
    SetStatusHeader(context, status);
    await response.InvokeAsync(context);
  }

  public void SetStatusHeader(HttpContext context, CachingProxyStatus status)
  {
    context.Response.Headers[CachingProxyConstants.StatusHeader] = status.ToString();
    metrics.IncrementRequests(status);
  }

  private async Task<HttpResponseMessage?> InternalServerError(HttpContext context, EventId eventId, string message, Exception? exception = null)
  {
    logger.LogError(eventId, exception, "{Message}", message);
    // return 503 Service Unavailable, since the client will most likely not retry it with 5xx error codes
    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    context.Response.ContentType = MediaTypeNames.Text.Plain;
    await context.Response.WriteAsync(message);
    return null;
  }
}

public enum RevalidationOutcome
{
  /// <summary>Upstream returned a new representation (2xx); the stored copy must be replaced.</summary>
  Replaced,
  /// <summary>Upstream returned 304; the stored copy is still valid and should be kept.</summary>
  NotModified,
  /// <summary>Upstream returned 404; the stored copy should be deleted.</summary>
  Gone,
  /// <summary>Upstream could not be reached or returned another error; serve the stale copy.</summary>
  UpstreamError,
}

/// <summary>
/// Result of <see cref="RemoteProxy.RevalidateAsync"/>. <see cref="Response"/> is non-null and open
/// only for <see cref="RevalidationOutcome.Replaced"/>, and the caller must dispose it.
/// </summary>
public sealed record RevalidationResult(RevalidationOutcome Outcome, HttpResponseMessage? Response = null);
