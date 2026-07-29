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

  protected override Task<AuthenticateResult> HandleAuthenticateAsync()
  {
    // cr_exp is used verbatim (as the string that arrived on the wire) in the signed payload, so a
    // valid signature pins the exact bytes the redirector signed; it is only additionally parsed as a
    // number for the expiry check.
    var expiry = Request.Query[ExpiryQueryParam].ToString();
    var signature = Request.Query[SignatureQueryParam].ToString();
    if (string.IsNullOrEmpty(expiry) || string.IsNullOrEmpty(signature))
      return Task.FromResult(AuthenticateResult.NoResult());

    if (!long.TryParse(expiry, out var expiryUnixSeconds))
      return Fail("cr_exp is not an integer");

    // Compare Unix seconds directly. DateTimeOffset.FromUnixTimeSeconds would throw for attacker-supplied
    // values outside its range, turning a bad credential into a 500 response.
    var nowUnixSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds();
    if (expiryUnixSeconds < nowUnixSeconds - Options.Config.ClockSkew.TotalSeconds)
      return Fail("signature expired");
    if (expiryUnixSeconds > nowUnixSeconds + Options.Config.MaxLifetime.TotalSeconds + Options.Config.ClockSkew.TotalSeconds)
      return Fail("signature expiry exceeds the maximum lifetime");

    byte[] providedSignature;
    try
    {
      providedSignature = Base64UrlEncoder.DecodeBytes(signature);
    }
    catch (Exception)
    {
      return Fail("cr_sig is not valid base64url");
    }

    var signedPayload = Encoding.UTF8.GetBytes($"{PathAndQueryWithoutSignatureParams()}\n{expiry}");

    // Try each key: the first is the active one, any later one a retiring key still honoured mid-rotation.
    // Each comparison is constant-time; the number of them reveals only which key matched, not secret.
    var keys = Options.KeyRing.Keys;
    for (var i = 0; i < keys.Length; i++)
    {
      if (!CryptographicOperations.FixedTimeEquals(
            providedSignature, HMACSHA256.HashData(keys[i].Bytes, signedPayload)))
        continue;

      // Counted, not logged: a retiring-key match is a successful request on every artifact fetch.
      metrics.IncrementRedirectSignatureVerification(i == 0 ? "active" : "retiring", keys[i].Fingerprint);

      // Establish an authenticated (but claim-less) principal: it satisfies the prefix's [Authorize] and,
      // just like a JWT-authenticated request, marks the response Cache-Control: private so the private
      // artifact is not stored by shared caches.
      var principal = new ClaimsPrincipal(new ClaimsIdentity(SchemeName));
      return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    // Its own metric tag because mid-rotation this means the steps ran out of order — the signer moved to
    // a key never staged here. The ring's fingerprints are not secret, so log them to make that visible.
    metrics.IncrementRedirectSignatureVerification("mismatch", "none");
    return Fail($"signature mismatch (tried {keys.Length} key(s), fingerprints: {Options.KeyRing})");
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
    var query = rawTarget[(queryStart + 1)..];

    var kept = new StringBuilder();
    foreach (var segment in query.Split('&'))
    {
      var eq = segment.IndexOf('=');
      var name = eq < 0 ? segment : segment[..eq];
      if (name is ExpiryQueryParam or SignatureQueryParam)
        continue;
      if (kept.Length > 0)
        kept.Append('&');
      kept.Append(segment);
    }

    return kept.Length > 0 ? $"{path}?{kept}" : path;
  }

  private Task<AuthenticateResult> Fail(string reason)
  {
    Logger.LogWarning("Rejecting redirect signature: {Reason}", reason);
    return Task.FromResult(AuthenticateResult.Fail(reason));
  }

  // Mirror the JwtBearer/deny paths: a rejected signed request still advertises Basic so a Basic-only
  // client (Maven/Gradle/npm) can retry against the redirector with credentials.
  protected override Task HandleChallengeAsync(AuthenticationProperties properties)
  {
    Response.StatusCode = StatusCodes.Status401Unauthorized;
    if (Options.Challenge is {} challenge)
      Response.Headers.Append(HeaderNames.WWWAuthenticate, challenge);
    return Task.CompletedTask;
  }
}
