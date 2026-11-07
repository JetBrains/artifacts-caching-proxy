using System;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JetBrains.CachingProxy;

public class RedirectSignatureOptions : AuthenticationSchemeOptions
{
  // Set from InboundAuthConfig.RedirectSignature (see AuthExtensions.AddInboundAuth). Non-null
  // whenever this scheme is registered.
  public CachingProxyConfig.RedirectSignatureConfig Config { get; set; } = null!;

  // Config.Key parsed and validated once at startup, so the per-request path never re-encodes key
  // material. Non-null whenever this scheme is registered.
  public RedirectSignatureKeyRing KeyRing { get; set; } = null!;

  public string? Challenge { get; set; }

  /// <summary>
  /// Where a gated request with an expired signature of ours is sent to be signed again: the redirector,
  /// <see cref="CachingProxyConfig.PublicUrl"/>. Null answers it 401 like any other failure.
  /// </summary>
  public Uri? RedirectorUrl { get; set; }
}

/// <summary>
/// Validates HMAC-signed redirects issued by the cache-redirector. The redirector, after validating a
/// client JWT itself, hands the client a 307 to this proxy with two extra query parameters:
/// <c>cr_exp</c> (Unix-seconds expiry) and <c>cr_sig</c>
/// (<c>base64url(HMAC-SHA256(key, path_and_query + "\n" + cr_exp))</c>), where <c>path_and_query</c> is
/// the request line as it arrives here with the two <c>cr_*</c> parameters removed. Because the client
/// follows the 307 to a different host, its <c>Authorization</c> header (the JWT) is dropped en route, so
/// this signature is the only credential a redirected request carries — it must therefore be sufficient
/// on its own to satisfy the prefix's <c>[Authorize]</c>. Direct clients presenting a JWT go through the
/// JwtBearer scheme instead; the forwarding policy scheme in <see cref="AuthExtensions"/> routes between
/// the two on the presence of <c>cr_sig</c>.
/// <para>
/// The signature is checked against every key in <see cref="RedirectSignatureKeyRing"/> so that a rotation
/// has an overlap window. Which key matched is reported (log + metric) but never taken from the request.
/// </para>
/// </summary>
public sealed class RedirectSignatureAuthenticationHandler(
  IOptionsMonitor<RedirectSignatureOptions> options, ILoggerFactory logger, UrlEncoder encoder,
  TimeProvider timeProvider, CachingProxyMetrics metrics)
  : AuthenticationHandler<RedirectSignatureOptions>(options, logger, encoder)
{
  public const string SchemeName = "RedirectSignature";
  public const string ExpiryQueryParam = "cr_exp";
  public const string SignatureQueryParam = "cr_sig";

  /// <summary>
  /// MRI-4855: the host of the redirector deployment that signed the request, when it is not the one
  /// <see cref="CachingProxyConfig.PublicUrl"/> names. Sandbox and Production share this proxy, so this is how a link it
  /// rewrites leads back to Sandbox. Unlike the other two it is covered by the signature, and it is honoured
  /// only on a request whose signature verified.
  /// </summary>
  public const string PublicHostQueryParam = "cr_host";

  protected override Task<AuthenticateResult> HandleAuthenticateAsync()
  {
    var result = Verify();

    // MRI-5711 shadow check: what the CloudFront edge decided about this same signature, if it passed one.
    if (!result.None)
    {
      var edgeVerdict = Request.Headers[CachingProxyConstants.EdgeVerdictHeader];
      metrics.IncrementEdgeSignatureVerdict(edgeVerdict.Count > 0 ? edgeVerdict.ToString() : null, result.Succeeded);
    }

    return Task.FromResult(result);
  }

  private AuthenticateResult Verify()
  {
    // cr_exp is used verbatim (as the string that arrived on the wire) in the signed payload, so a
    // valid signature pins the exact bytes the redirector signed; it is only additionally parsed as a
    // number for the expiry check.
    var expiry = Request.Query[ExpiryQueryParam].ToString();
    var signature = Request.Query[SignatureQueryParam].ToString();
    if (string.IsNullOrEmpty(expiry) || string.IsNullOrEmpty(signature))
      return AuthenticateResult.NoResult();

    if (!long.TryParse(expiry, out var expiryUnixSeconds))
      return Fail("cr_exp is not an integer");

    // Compare Unix seconds directly. DateTimeOffset.FromUnixTimeSeconds would throw for attacker-supplied
    // values outside its range, turning a bad credential into a 500 response.
    var nowUnixSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds();
    if (expiryUnixSeconds < nowUnixSeconds - Options.Config.ClockSkew.TotalSeconds)
      return Expired(signature, expiry);
    if (expiryUnixSeconds > nowUnixSeconds + Options.Config.MaxLifetime.TotalSeconds + Options.Config.ClockSkew.TotalSeconds)
      return Fail("signature expiry exceeds the maximum lifetime");

    if (MatchingKey(signature, expiry) is not { } i)
      return Fail("cr_sig is not valid base64url");

    var keys = Options.KeyRing.Keys;
    if (i >= 0)
    {
      // Counted, not logged: a retiring-key match is a successful request on every artifact fetch.
      metrics.IncrementRedirectSignatureVerification(i == 0 ? "active" : "retiring", keys[i].Fingerprint);

      // Only a DNS name: it becomes the authority of the links we store. A signed bad value is a redirector
      // bug, so the request is still served, with the configured host.
      var publicHost = Request.Query[PublicHostQueryParam].ToString();
      if (publicHost.Length > 0)
      {
        if (Uri.CheckHostName(publicHost) == UriHostNameType.Dns)
          Context.Items[CachingProxyConstants.PublicHostItemKey] = publicHost.ToLowerInvariant();
        else
          Logger.LogWarning("Ignoring signed {Param} that is not a host name: {Value}", PublicHostQueryParam, publicHost);
      }

      // Establish an authenticated (but claim-less) principal: it satisfies the prefix's [Authorize] and,
      // just like a JWT-authenticated request, marks the response Cache-Control: private so the private
      // artifact is not stored by shared caches.
      var principal = new ClaimsPrincipal(new ClaimsIdentity(SchemeName));
      return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    // Its own metric tag because mid-rotation this means the steps ran out of order — the signer moved to
    // a key never staged here. The ring's fingerprints are not secret, so log them to make that visible.
    metrics.IncrementRedirectSignatureVerification("mismatch", "none");
    return Fail($"signature mismatch (tried {keys.Length} key(s), fingerprints: {Options.KeyRing})");
  }

  // The index of the ring key the signature verifies with, -1 for none, or null when cr_sig is not base64url.
  // Try each key: the first is the active one, any later one a retiring key still honoured mid-rotation.
  // Each comparison is constant-time; the number of them reveals only which key matched, not secret.
  private int? MatchingKey(string signature, string expiry)
  {
    byte[] providedSignature;
    try
    {
      providedSignature = Base64UrlEncoder.DecodeBytes(signature);
    }
    catch (Exception)
    {
      return null;
    }

    var signedPayload = Encoding.UTF8.GetBytes($"{PathAndQueryWithoutSignatureParams()}\n{expiry}");
    var keys = Options.KeyRing.Keys;
    for (var i = 0; i < keys.Length; i++)
      if (CryptographicOperations.FixedTimeEquals(providedSignature, HMACSHA256.HashData(keys[i].Bytes, signedPayload)))
        return i;
    return -1;
  }

  /// <summary>
  /// An expired signature: a failure, and for one we signed, the redirector URL to renew it at - see
  /// <see cref="HandleChallengeAsync"/>. Ours is the common case, a client resuming a slow download on the
  /// signed URL after it expired (CACHE-REDIRECTOR-4Q), so it is logged as information when it can be renewed.
  /// </summary>
  private AuthenticateResult Expired(string signature, string expiry)
  {
    if (MatchingKey(signature, expiry) is not >= 0 || RedirectorUrlForThisRequest() is not { } renewAt)
      return Fail("signature expired");

    Context.Items[RenewAtItemKey] = renewAt;
    Logger.LogInformation("Redirect signature expired; a gated request is sent back to {RenewAt}", renewAt);
    return AuthenticateResult.Fail("signature expired");
  }

  private const string RenewAtItemKey = "RedirectSignature.RenewAt";

  // The redirector URL this request was redirected from: the same path, which is the redirector alias, on the
  // signed cr_host when it names one (Sandbox) or on RedirectorUrl, with the query minus our cr_* parameters.
  // Only called once the signature verified, so cr_host is ours.
  private string? RedirectorUrlForThisRequest()
  {
    var publicHost = Request.Query[PublicHostQueryParam].ToString();
    var root = publicHost.Length > 0 && Uri.CheckHostName(publicHost) == UriHostNameType.Dns
      ? $"https://{publicHost.ToLowerInvariant()}/"
      : Options.RedirectorUrl?.AbsoluteUri;
    if (root == null)
      return null;

    var rawTarget = Context.Features.Get<IHttpRequestFeature>()?.RawTarget;
    if (string.IsNullOrEmpty(rawTarget))
      rawTarget = Request.Path + Request.QueryString;
    var queryStart = rawTarget.IndexOf('?');
    var path = (queryStart < 0 ? rawTarget : rawTarget[..queryStart]).TrimStart('/');
    var kept = queryStart < 0 ? "" : KeepNonSignatureParams(rawTarget[(queryStart + 1)..], dropPublicHost: true);
    return kept.Length > 0 ? $"{root}{path}?{kept}" : $"{root}{path}";
  }

  // Reconstruct the exact string the redirector signed: the raw (undecoded) request line with the
  // trailing cr_exp/cr_sig parameters stripped. Operating on the raw target byte-matches how the
  // redirector built path_and_query from nginx's $request_uri, avoiding any decode/re-encode drift.
  private string PathAndQueryWithoutSignatureParams()
  {
    var rawTarget = Context.Features.Get<IHttpRequestFeature>()?.RawTarget;
    if (string.IsNullOrEmpty(rawTarget))
      rawTarget = Request.Path + Request.QueryString;

    var queryStart = rawTarget.IndexOf('?');
    if (queryStart < 0)
      return rawTarget;

    var path = rawTarget[..queryStart];
    var kept = KeepNonSignatureParams(rawTarget[(queryStart + 1)..], dropPublicHost: false);
    return kept.Length > 0 ? $"{path}?{kept}" : path;
  }

  /// <summary>
  /// The request's own query string, in <c>?a=1</c> form and empty when nothing is left, with the
  /// <c>cr_exp</c>/<c>cr_sig</c> pair removed. Those two are a credential for <i>this</i> proxy, minted
  /// for one path and one expiry, so anything that forwards a query somewhere else - a Redirect rule's
  /// <c>Location</c>, see <see cref="RemoteProxy"/> - has to drop them first: sent on they would both
  /// disclose our signature to the origin and leave it replayable here until it expires. <c>cr_host</c> goes
  /// too: it is ours, and means nothing to an origin.
  /// </summary>
  public static string QueryWithoutSignatureParams(HttpRequest request)
  {
    var query = request.QueryString.Value;
    if (string.IsNullOrEmpty(query))
      return "";

    var kept = KeepNonSignatureParams(query.TrimStart('?'), dropPublicHost: true);
    return kept.Length > 0 ? $"?{kept}" : "";
  }

  // The query segments, verbatim and in order, minus the two cr_* parameters, and minus cr_host when
  // dropPublicHost (it is signed, so the payload keeps it). Takes and returns a query without its leading '?'.
  private static string KeepNonSignatureParams(string query, bool dropPublicHost)
  {
    var kept = new StringBuilder();
    foreach (var segment in query.Split('&'))
    {
      var eq = segment.IndexOf('=');
      var name = eq < 0 ? segment : segment[..eq];
      if (name is ExpiryQueryParam or SignatureQueryParam || (dropPublicHost && name == PublicHostQueryParam))
        continue;
      if (kept.Length > 0)
        kept.Append('&');
      kept.Append(segment);
    }

    return kept.ToString();
  }

  private AuthenticateResult Fail(string reason)
  {
    Logger.LogWarning("Rejecting redirect signature: {Reason}", reason);
    return AuthenticateResult.Fail(reason);
  }

  // Mirror the JwtBearer/deny paths: a rejected signed request still advertises Basic so a Basic-only
  // client (Maven/Gradle/npm) can retry against the redirector with credentials.
  //
  // An expired signature of ours is renewed instead: a 307 back to the redirector, which checks the client
  // again and signs a fresh redirect here. Never stored: the edge cache key leaves out cr_exp and cr_sig, so
  // a stored 307 would bounce the fresh signature too.
  protected override Task HandleChallengeAsync(AuthenticationProperties properties)
  {
    if (Context.Items[RenewAtItemKey] is string renewAt)
    {
      Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
      Response.Headers.Location = renewAt;
      Response.Headers.CacheControl = "no-store";
      return Task.CompletedTask;
    }

    Response.StatusCode = StatusCodes.Status401Unauthorized;
    if (Options.Challenge is {} challenge)
      Response.Headers.Append(HeaderNames.WWWAuthenticate, challenge);
    return Task.CompletedTask;
  }
}
