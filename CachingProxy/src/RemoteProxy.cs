using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Duende.AccessTokenManagement;
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
  IClientCredentialsTokenManager? tokenManager = null)
{
  [GeneratedRegex(@"^([\x20a-zA-Z_\-0-9./+@]|%[0-9a-fA-F]{2})+$", RegexOptions.Compiled)]
  private static partial Regex OurGoodPathChars { get; }

  private readonly Regex? myBlacklistRegex = !string.IsNullOrWhiteSpace(config.BlacklistUrlRegex) ?
    new Regex(config.BlacklistUrlRegex, RegexOptions.Compiled) : null;

  private readonly Regex? myRedirectToRemoteUrlsRegex = !string.IsNullOrWhiteSpace(config.RedirectToRemoteUrlsRegex) ?
    new Regex(config.RedirectToRemoteUrlsRegex, RegexOptions.Compiled) : null;

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
  public async Task<HttpResponseMessage?> ProcessAsync(HttpContext context, string cacheKey, CacheDuration cacheDuration, Uri upstreamUri, string? contentType = null, UpstreamAuth? auth = null)
  {
    var isHead = HttpMethods.IsHead(context.Request.Method);

    var cachedResponse = await responseCache.GetCachedStatusCode(cacheKey, context.RequestAborted);
    switch (cachedResponse?.StatusCode)
    {
      case >= HttpStatusCode.BadRequest:
        await SetStatusAsync(context, CachingProxyStatus.NEGATIVE_HIT,
          cachedResponse with { StatusCode = ClientFacingStatus(cachedResponse.StatusCode) });
        return null;

      // The caller decides whether the key includes the HTTP method (the S3 backend does so for
      // signed links), so a verb-specific presigned redirect only ever replays for the same verb it
      // was stored under. A cached 2xx is replayed when it carries the full body (the S3 backend
      // inlines small objects into the cache) or when the request is a HEAD (which needs only the
      // metadata); a bodyless 2xx is never replayed to a GET, whose body lives on disk/S3 instead.
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

    // Mutable / non-cacheable paths (SNAPSHOTs, maven-metadata.xml, npm security) are redirected to the
    // origin with 307 (RedirectKeepVerb) instead of being cached - this holds for authenticated upstreams
    // too: a 307 preserves the method and the client reuses its own credentials for the origin, so there
    // is no need to proxy these through (which would wrongly cache mutable content for protected sources).
    // A credential-less auth entry (external-auth / redirect-only, e.g. CloudFront) is handled the same
    // way for every path: the proxy holds no upstream credentials, so the client is redirected to
    // authenticate directly with the origin, which serves per-user content the proxy must never cache.
    var isRedirectToRemoteUrl = myRedirectToRemoteUrlsRegex != null && myRedirectToRemoteUrlsRegex.IsMatch(requestPath);
    var isExternalAuth = auth is { HasCredentials: false };
    if (isRedirectToRemoteUrl || isExternalAuth)
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
      if (tokenManager != null && auth != null)
      {
        request.Headers.Authorization = await tokenManager.GetUpstreamAuthorizationHeaderAsync(auth, context.RequestAborted);
      }
      response = await httpClient.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    }
    catch (OperationCanceledException canceledException)
    {
      if (context.RequestAborted == canceledException.CancellationToken) throw;

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
        if (ClientFacingStatus(response.StatusCode) is var statusCode and HttpStatusCode.NotFound && response.StatusCode != statusCode)
        {
          logger.LogWarning(Event.NegativeMiss(response.StatusCode),
            "Non-success requesting {UpstreamUri}: {StatusCode}", upstreamUri, response.StatusCode);
          entry = entry with { StatusCode = statusCode };
        }

        await SetStatusAsync(context, CachingProxyStatus.NEGATIVE_MISS, entry);
        return null;
      }

      var headersContentEncoding = response.Content.Headers.ContentEncoding;
      if (headersContentEncoding.Count > 1)
        return await InternalServerError(context, Event.MultipleContentTypes,
          $"{upstreamUri} returned multiple Content-Encoding which is not allowed: {string.Join(", ", headersContentEncoding)}");

      var contentEncoding = headersContentEncoding.Count == 0 ? null : headersContentEncoding.Single();
      if (contentEncoding != null && contentEncoding != "gzip")
        return await InternalServerError(context, Event.NotSupportedContentType,
          $"{upstreamUri} returned Content-Encoding '{contentEncoding}' which is not supported");

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
