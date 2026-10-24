namespace JetBrains.CachingProxy
{
  public static class CachingProxyConstants
  {
    public const string StatusHeader = "X-Artifact-Caching-Proxy";
    public const string CachedStatusHeader = "X-Artifact-Caching-Proxy-Cached-Status";
    public const string CachedUntilHeader = "X-Artifact-Caching-Proxy-Cached-Until";
    public const string CachedContentLengthHeader = "X-Artifact-Caching-Proxy-Cached-Content-Length";

    // HttpContext.Items key holding the freshness window (TimeSpan) resolved from the request's
    // caching-profile rule. When present it is advertised to the client as Cache-Control max-age
    // instead of the eternal 365-day default (see CachedResponse.GetCachingHeader).
    public const string RefreshAfterItemKey = "X-Artifact-Caching-Proxy-RefreshAfter";

    // How an OCI client learns it is talking to a distribution v2 registry: sent on our own /v2/ ping and
    // relayed from an upstream registry through the cache (see CachedResponse).
    public const string DockerApiVersionHeader = "Docker-Distribution-API-Version";
    public const string DockerApiVersion = "registry/2.0";

    // MRI-5711, edge verification of signed redirects (docs/design/edge-verified-signed-redirects.md in
    // Cache-Redirector). The CloudFront function verify-signed-redirect sets EdgeVerified ("1") on a request
    // whose cr_sig it verified, and EdgeVerdict (its outcome) on every request it sees. A response flagged
    // EdgeCacheable is public for CloudFront alone: restore-private-cache-control strips the flag and makes
    // it private again before it reaches a viewer.
    public const string EdgeVerifiedHeader = "X-CR-Edge-Verified";
    public const string EdgeVerdictHeader = "X-CR-Edge-Verdict";
    public const string EdgeCacheableHeader = "X-CR-Edge-Cacheable";
  }
}
