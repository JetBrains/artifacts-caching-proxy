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

    // How an OCI client learns it is talking to a distribution v2 registry: sent on our own /v2/ ping and
    // relayed from an upstream registry through the cache (see CachedResponse).
    public const string DockerApiVersionHeader = "Docker-Distribution-API-Version";
    public const string DockerApiVersion = "registry/2.0";
  }
}
