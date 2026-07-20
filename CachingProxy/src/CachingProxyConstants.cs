namespace JetBrains.CachingProxy
{
  public static class CachingProxyConstants
  {
    public const string StatusHeader = "X-Artifact-Caching-Proxy";
    public const string CachedStatusHeader = "X-Artifact-Caching-Proxy-Cached-Status";
    public const string CachedUntilHeader = "X-Artifact-Caching-Proxy-Cached-Until";

    // HttpContext.Items key holding the freshness window (TimeSpan) resolved from the request's
    // caching-profile rule. When present it is advertised to the client as Cache-Control max-age
    // instead of the eternal 365-day default (see CachedResponse.GetCachingHeader).
    public const string RefreshAfterItemKey = "X-Artifact-Caching-Proxy-RefreshAfter";
  }
}
