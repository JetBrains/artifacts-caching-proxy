using System;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Duende.AccessTokenManagement;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JetBrains.CachingProxy;

/// <summary>
/// Outbound (per-upstream) and inbound (client JWT) authentication wiring, split out of Program so the
/// startup file stays focused on the cache/storage/observability pipeline.
/// </summary>
public static class AuthExtensions
{
  /// <summary>
  /// Per-upstream auth. Two credential modes coexist (see <see cref="UpstreamAuth"/>): OAuth
  /// client-credentials (Duende token management, one client per entry named by its ClientId) and GitHub
  /// App (a signed JWT exchanged for an installation token — GitHub does not support the client-credentials
  /// grant). The dispatch between them lives in <see cref="IUpstreamAuthorizationProvider"/>, which
  /// RemoteProxy resolves optionally, so unauthenticated deployments pull in nothing extra.
  /// </summary>
  public static IServiceCollection AddUpstreamAuth(this IServiceCollection services, IConfiguration configuration)
  {
    ClientCredentialsTokenManagementBuilder? tokenManagement = null;
    foreach (var (name, auth) in configuration.Get<CachingProxyConfig>()?.UpstreamAuth ?? [])
    {
      if (string.IsNullOrEmpty(auth.ClientId))
        continue;

      // GitHub App mode (ClientId is the JWT issuer, PrivateKey is the signing key): the installation
      // token is minted on demand by GitHubAppInstallationTokenProvider, so there is no Duende client to
      // register here. TokenEndpoint/ClientSecret are not used.
      if (auth.IsGitHubApp)
        continue;

      tokenManagement ??= services.AddClientCredentialsTokenManagement();

      // ClientId is set without a private key, so this is client-credentials mode: the token endpoint and
      // secret are mandatory. Fail fast rather than sending a null token endpoint / unauthenticated request.
      if (auth.TokenEndpoint == null || string.IsNullOrEmpty(auth.ClientSecret))
        throw new ArgumentException(
          $"UpstreamAuth '{name}' sets ClientId but is missing TokenEndpoint and/or ClientSecret; " +
          "both are required for client-credentials auth.");

      tokenManagement.AddClient(auth.ClientId!, client =>
      {
        client.TokenEndpoint = auth.TokenEndpoint;
        client.ClientId = ClientId.Parse(auth.ClientId!);
        client.ClientSecret = ClientSecret.Parse(auth.ClientSecret);
        if (!string.IsNullOrEmpty(auth.Scope))
          client.Scope = Scope.Parse(auth.Scope);
      });
    }

    // GitHub App installation-token provider plus its dedicated GitHub REST client.
    // Registered whenever any upstream auth exists — harmless when no GitHub App entry is configured. The
    // dispatch provider also resolves the (optional) Duende token manager.
    services.AddSingleton<GitHubAppInstallationTokenProvider>();
    services.AddSingleton<IUpstreamAuthorizationProvider, UpstreamAuthorizationProvider>();

    return services;
  }

  /// <summary>
  /// Inbound JWT bearer validation. Authentication/authorization are always registered, since a prefix
  /// whose upstream requires auth carries an AuthorizeAttribute (attached per prefix in RemoteServers)
  /// regardless of inbound config. When InboundAuth is configured, the JWT bearer scheme validates
  /// issuer/audience/lifetime explicitly; the signing keys come from the configured JWKS endpoint via a
  /// ConfigurationManager that caches and auto-refreshes them (so key rotation needs no redeploy). When it
  /// is NOT configured, a fail-closed default scheme (<see cref="DenyAuthenticationHandler"/>) answers any
  /// AuthorizeAttribute challenge with 401 instead of letting the middleware throw a 500.
  /// </summary>
  public static IServiceCollection AddInboundAuth(this IServiceCollection services, IConfiguration configuration)
  {
    services.AddAuthorization();

    var inboundAuth = configuration.Get<CachingProxyConfig>()?.InboundAuth;
    if (inboundAuth == null)
    {
      // No inbound validation, but a matched-upstream prefix still carries [Authorize]. Register a
      // no-identity default scheme so those requests fail closed with 401 (public prefixes stay anonymous).
      services
        .AddAuthentication(DenyAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DenyAuthenticationHandler>(DenyAuthenticationHandler.SchemeName, null);
      return services;
    }

    var jwks = new ConfigurationManager<OpenIdConnectConfiguration>(
      inboundAuth.JwksUrl.AbsoluteUri,
      new JwksConfigurationRetriever(),
      new HttpDocumentRetriever { RequireHttps = inboundAuth.JwksUrl.Scheme == Uri.UriSchemeHttps });

    services
      .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
      .AddJwtBearer(options =>
      {
        options.ConfigurationManager = jwks;
        options.TokenValidationParameters = new TokenValidationParameters
        {
          ValidateIssuer = true,
          ValidIssuer = inboundAuth.Issuer,
          ValidateAudience = true,
          ValidAudience = inboundAuth.Audience,
          ValidateIssuerSigningKey = true,
          ValidateLifetime = true,
          RequireExpirationTime = inboundAuth.RequireExpiration,
        };
        // Accept the JWT carried as the HTTP Basic password too, not just "Authorization: Bearer
        // <jwt>". Clients that only speak Basic (Maven/Gradle/npm) put the token in the password.
        options.Events = new JwtBearerEvents
        {
          OnMessageReceived = context =>
          {
            if (string.IsNullOrEmpty(context.Token))
              context.Token = GetTokenFromBasicPassword(context.Request.Headers.Authorization.ToString());
            return Task.CompletedTask;
          },
        };
      });

    return services;
  }

  /// <summary>
  /// Adds the authentication/authorization middleware. Must run after UseRouting (so the matched
  /// endpoint's AuthorizeAttribute metadata is available) and before the proxy middleware (which would
  /// otherwise serve unauthenticated requests). /health stays public (handled earlier in the pipeline).
  /// </summary>
  public static IApplicationBuilder UseInboundAuth(this IApplicationBuilder app, CachingProxyConfig config)
  {
    app.UseAuthentication();
    app.UseAuthorization();
    return app;
  }

  // Extracts the password from an "Authorization: Basic base64(user:password)" header, used to accept
  // a JWT that a Basic-only client (Maven/Gradle/npm) sent as the password. Returns null when the
  // header is absent, not Basic, malformed, or carries no password.
  private static string? GetTokenFromBasicPassword(string? authorizationHeader)
  {
    if (!AuthenticationHeaderValue.TryParse(authorizationHeader, out var header) ||
        !"Basic".Equals(header.Scheme, StringComparison.OrdinalIgnoreCase) ||
        header.Parameter is not { Length: > 0 } parameter)
      return null;

    try
    {
      var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
      var separator = decoded.IndexOf(':');
      return separator >= 0 && separator < decoded.Length - 1 ? decoded[(separator + 1)..] : null;
    }
    catch (FormatException)
    {
      return null;
    }
  }
}

// Default authentication scheme used when no inbound JWT validation is configured. Never establishes an
// identity (so un-gated/public prefixes stay anonymous) and answers a challenge with a bare 401, so a
// prefix that carries [Authorize] (its upstream requires auth) fails closed instead of throwing a 500.
internal sealed class DenyAuthenticationHandler(
  IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
  : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
  public const string SchemeName = "Deny";

  protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
    Task.FromResult(AuthenticateResult.NoResult());

  protected override Task HandleChallengeAsync(AuthenticationProperties properties)
  {
    Response.StatusCode = StatusCodes.Status401Unauthorized;
    return Task.CompletedTask;
  }
}
