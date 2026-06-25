using System;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Duende.AccessTokenManagement;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
  /// RemoteProxy resolves optionally, so unauthenticated deployments pull in nothing extra. Credential-less
  /// entries (redirect-only / external auth) register no client — they never fetch a token.
  /// </summary>
  public static IServiceCollection AddUpstreamAuth(this IServiceCollection services, IConfiguration configuration)
  {
    var upstreamAuth = configuration.Get<CachingProxyConfig>()?.UpstreamAuth ?? [];
    if (upstreamAuth.Length == 0)
      return services;

    // Only stand up Duende client-credentials management when at least one entry needs it; a GitHub-App-only
    // (or redirect-only) deployment skips it entirely.
    var hasClientCredentialEntry = false;
    foreach (var auth in upstreamAuth)
      if (auth is { HasCredentials: true, IsGitHubApp: false }) { hasClientCredentialEntry = true; break; }

    var tokenManagement = hasClientCredentialEntry ? services.AddClientCredentialsTokenManagement() : null;
    foreach (var auth in upstreamAuth)
    {
      if (!auth.HasCredentials)
        continue;

      // GitHub App mode (ClientId is the JWT issuer, PrivateKey is the signing key): the installation
      // token is minted on demand by GitHubAppInstallationTokenProvider, so there is no Duende client to
      // register here. TokenEndpoint/ClientSecret are not used.
      if (auth.IsGitHubApp)
        continue;

      // ClientId is set without a private key, so this is client-credentials mode: the token endpoint and
      // secret are mandatory. Fail fast rather than sending a null token endpoint / unauthenticated request.
      if (auth.TokenEndpoint == null || string.IsNullOrEmpty(auth.ClientSecret))
        throw new ArgumentException(
          $"UpstreamAuth '{auth.UrlPrefix}' sets ClientId but is missing TokenEndpoint and/or ClientSecret; " +
          "both are required for client-credentials auth. Omit ClientId for a redirect-only (external auth) entry.");

      tokenManagement!.AddClient(auth.ClientId!, client =>
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
  /// Inbound JWT bearer validation. Registered only when InboundAuth is configured, so deployments without
  /// inbound auth pull in nothing extra. Issuer/audience/lifetime are validated explicitly; the signing
  /// keys come from the configured JWKS endpoint via a ConfigurationManager that caches and auto-refreshes
  /// them (so key rotation needs no redeploy). The matching AuthorizeAttribute is attached per prefix in
  /// RemoteServers when the prefix's upstream requires auth.
  /// </summary>
  public static IServiceCollection AddInboundAuth(this IServiceCollection services, IConfiguration configuration)
  {
    var inboundAuth = configuration.Get<CachingProxyConfig>()?.InboundAuth;
    if (inboundAuth == null)
      return services;

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
    services.AddAuthorization();

    return services;
  }

  /// <summary>
  /// Adds the authentication/authorization middleware when inbound auth is configured. Must run after
  /// UseRouting (so the matched endpoint's AuthorizeAttribute metadata is available) and before the proxy
  /// middleware (which would otherwise serve unauthenticated requests). /health stays public (handled
  /// earlier in the pipeline).
  /// </summary>
  public static IApplicationBuilder UseInboundAuth(this IApplicationBuilder app, CachingProxyConfig config)
  {
    if (config.InboundAuth != null)
    {
      app.UseAuthentication();
      app.UseAuthorization();
    }
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
