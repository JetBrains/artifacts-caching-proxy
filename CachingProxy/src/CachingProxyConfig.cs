using System;
using System.Collections.Generic;
using System.IO;

namespace JetBrains.CachingProxy;

public class CachingProxyConfig
{
  public record S3Config(string? BucketName = null, bool SignedLinks = false)
  {
    // Lifetime of a presigned redirect link. The link is (re)signed on every request that serves the
    // redirect (including cache HITs), so it only needs to outlive a single client's redirect-follow,
    // not the cached redirect entry — a short TTL is both sufficient and safe.
    public TimeSpan SignedLinkTTL { get; init; } = TimeSpan.FromMinutes(10);

    // Objects whose body fits within this many bytes are probed in a single ranged GET and served
    // inline (200 + body, cached in L1/L2); larger objects are redirected to S3. Raising it trades
    // more probe bandwidth and cache footprint for fewer client round-trips on small/medium artifacts
    // (e.g. dependency metadata). Hosts have ample RAM headroom; the cost dimension is the L2 cache.
    public int InlineThresholdBytes { get; init; } = 32 * 1024;
  }

  public record RedisConfig(string? ConnectionString = null)
  {
    // Optional key prefix, useful when several apps share one Redis instance.
    public string? InstanceName { get; init; }
  }

  // Validation parameters for inbound client JWT bearer tokens. Issuer, audience and lifetime are
  // validated explicitly; the token-signing public keys are fetched from a JSON Web Key Set (JWKS)
  // endpoint (e.g. https://jetbrains.team/oauth/jwks.json) and cached/refreshed automatically, so key
  // rotation needs no redeploy. Any JWKS key type (RSA/EC) is accepted.
  public record InboundAuthConfig
  {
    public required string Issuer { get; init; }
    public required string[] Audiences { get; init; }
    public required Uri JwksUrl { get; init; }

    // Whether tokens must carry an 'exp' claim. Default true (any token without an expiration is
    // rejected). Set false to accept non-expiring tokens such as JetBrains hub permanent tokens; a
    // token that does carry exp/nbf is still validated against them. Trade-off: a leaked non-expiring
    // token stays valid until the signing key rotates or it is revoked at the issuer (not checked here).
    public bool RequireExpiration { get; init; } = true;

    // HMAC signature validation for redirects issued by the cache-redirector. When the redirector
    // hands a client a signed 307 to this proxy, the client follows it to a different host, so the
    // client JWT (Authorization header) is dropped by HTTP clients on the cross-host hop. The
    // redirector instead proves the request was authorized by appending cr_exp/cr_sig query
    // parameters signed with a key shared out-of-band (see the cache-redirector repo's auth.lua).
    // Null by default: no signature is accepted and only the JWT authorizes a private prefix.
    public RedirectSignatureConfig? RedirectSignature { get; init; }
  }

  // Shared-secret HMAC verification of cache-redirector signed links. The signature covers the
  // request line (path + query, minus the cr_exp/cr_sig parameters themselves) plus the expiry, so a
  // signed URL authorizes exactly one path until it expires and cannot be replayed against another.
  public record RedirectSignatureConfig
  {
    // The HMAC-SHA256 key, shared out-of-band with the redirector (its CR_SIGNING_KEY). Same value,
    // duplicated across the two AWS accounts (see the redirector repo's README).
    public required string Key { get; init; }

    // Reject correctly signed URLs that claim an unexpectedly distant expiry. This limits replay if a
    // redirect URL leaks and prevents a buggy redirector from accidentally minting effectively permanent
    // credentials. ClockSkew is applied in addition to this lifetime at validation time.
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(10);

    // Tolerance for clock drift between the redirector (which stamps cr_exp) and this proxy when
    // checking expiry. The signature lifetime itself is chosen by the redirector.
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);
  }

  public CachingProxyPrefix[] Prefixes { get; init; } = [];

  // OAuth client-credentials auth for private upstreams, matched to each prefix by longest URL
  // prefix (see RemoteServers). Empty by default: upstreams without a matching entry are requested
  // unauthenticated, exactly as before.
  public Dictionary<string, UpstreamAuth> UpstreamAuth { get; init; } = new();

  // Inbound JWT bearer validation, applied to every prefix whose upstream requires auth (i.e. has a
  // matching UpstreamAuth entry — see RemoteServers). Null by default: no inbound auth, every prefix
  // stays public, exactly as before.
  public InboundAuthConfig? InboundAuth { get; init; }

  public S3Config? S3 { get; init; }
  public RedisConfig? Redis { get; init; }
  public string LocalCachePath { get; init; } = Path.Combine(Path.GetTempPath(), "artifacts-caching-proxy");
  public string? BlacklistUrlRegex { get; init; }
  public long MinimumFreeDiskSpaceMb { get; init; } = 2048;
  public long RequestTimeoutSec { get; init; } = 20;

  // Upper bound on the TCP connect phase alone, separate from the whole-request RequestTimeoutSec.
  // Without it a connect that never completes (an upstream silently dropping SYNs while
  // rate-limiting, say) consumes the entire request budget and is reported as a timeout rather than
  // a connect failure.
  public long ConnectTimeoutSec { get; init; } = 10;

  // Cap on simultaneous upstream connections per origin (.NET's default is unlimited). Intended as
  // a runaway backstop rather than a throttle: a single slow origin would otherwise open unbounded
  // connections, and because an origin often resolves to one address they all compete for a source
  // port against the same (dst, dport) tuple until the ephemeral range is exhausted and connect()
  // fails with EADDRNOTAVAIL. Keep it well above peak concurrent in-flight requests per instance —
  // too low a value makes requests queue, hit RequestTimeoutSec, and get negative-cached as 404s.
  //
  // The default is sized from production: peak concurrent in-flight requests per instance across
  // *all* origins is ~212 (30d max of http.server.active_requests), so 1024 leaves ~5x headroom and
  // should never queue, while still being only ~4% of the ~28k ephemeral ports available for a
  // single (dst, dport) tuple.
  public int MaxConnectionsPerServer { get; init; } = 1024;

  // Named caching profiles, referenced per prefix by CachingProxyPrefix.Profile. A profile decides,
  // per request path, whether an endpoint is cached (and for how long before revalidation) or
  // redirected to the upstream — replacing the former single global RedirectToRemoteUrlsRegex. A
  // prefix with no profile caches every path forever (the immutable default). Empty by default.
  public Dictionary<string, CachingProfile> CachingProfiles { get; init; } = new();

  public string? UserAgentComment { get; init; }

  public string? CleanupInterval { get; init; }
  public TimeSpan CleanupPeriod { get; init; } = TimeSpan.FromDays(7);

  public CacheDuration CacheDuration { get; init; } = new();

  // Global per-status TTLs for the L2 (distributed/Redis) cache, mirroring CacheDuration (same
  // defaults). Configured globally only, never per prefix. The effective L2 duration for a status
  // code is max(L1, L2): L2 is never shorter than L1, so the durable backing store never expires
  // before the in-process copy.
  public CacheDuration DistributedCacheDuration { get; init; } = new();
}
