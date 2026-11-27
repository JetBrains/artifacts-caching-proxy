namespace JetBrains.CachingProxy;

public enum CachingProxyStatus
{
  HIT,
  NEGATIVE_HIT,
  MISS,
  NEGATIVE_MISS,
  BLACKLISTED,
  ALWAYS_REDIRECT,

  // A stale stored copy was revalidated against the upstream and served fresh (upstream 200 or 304).
  REVALIDATED,
  // A stale stored copy was served because the upstream revalidation failed (timeout/5xx/etc.).
  STALE,

  // An OCI request got no answer in time, from the upstream or behind another request for the same object, and
  // the client was redirected to the same URL to ask again. Nothing was cached. See OciPendingUpstreams.
  UPSTREAM_PENDING,

  BAD_REQUEST
}
