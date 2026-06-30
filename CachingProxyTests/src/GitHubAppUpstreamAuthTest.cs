using System;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// End-to-end coverage for GitHub App (server-to-server) upstream auth. A fake GitHub REST API records the
// JWT it is presented with and issues an installation token; a real upstream records the Authorization it
// receives. We assert the proxy (1) signs an app JWT with the configured private key (iss = client id),
// (2) auto-resolves the single installation, (3) sends the upstream Bearer <installation_token>, and
// (4) caches the installation token (the access_tokens endpoint is hit once for two fetches).
public class GitHubAppUpstreamAuthTest : IAsyncLifetime
{
  private const string AppClientId = "Iv23li-test-app";
  private const long InstallationId = 42;
  private const string InstallationToken = "ghs_installation_token";

  // Inbound JWT validation (the github prefix carries credentials, so its route also requires a client JWT).
  private const string Issuer = "https://issuer.example.com";
  private const string Audience = "artifacts-caching-proxy";
  private const string Kid = "test-key-1";
  private readonly RSA myInboundRsa = RSA.Create(2048);

  // The GitHub App's RSA private key, supplied to the proxy as PEM and used here to verify the app JWT.
  private readonly RSA myAppRsa = RSA.Create(2048);

  private readonly WebApplication myGitHubApi;
  private readonly WebApplication myUpstreamServer;
  private string myTempDirectory = "";
  private IHost? myProxyHost;

  private int myListInstallationsRequests;
  private int myAccessTokenRequests;
  private string myAccessTokenAuth = "";
  private string myAccessTokenInstallationId = "";
  private readonly ConcurrentDictionary<string, string> myUpstreamAuthByPath = new();

  public GitHubAppUpstreamAuthTest()
  {
    myGitHubApi = BuildKestrel(router =>
    {
      // Inbound JWKS (served here too, so the test needs only one auxiliary server).
      router.MapGet("jwks.json", (_, res, _) =>
      {
        res.ContentType = "application/json";
        return res.WriteAsync(JwksJson(myInboundRsa));
      });

      router.MapGet("app/installations", (_, res, _) =>
      {
        Interlocked.Increment(ref myListInstallationsRequests);
        res.ContentType = "application/json";
        return res.WriteAsync(JsonSerializer.Serialize(new[] { new { id = InstallationId } }));
      });

      router.MapPost("app/installations/{id}/access_tokens", (req, res, data) =>
      {
        Interlocked.Increment(ref myAccessTokenRequests);
        myAccessTokenAuth = req.Headers.Authorization.ToString();
        myAccessTokenInstallationId = (string)data.Values["id"]!;
        res.ContentType = "application/json";
        return res.WriteAsync(JsonSerializer.Serialize(new
        {
          token = InstallationToken,
          expires_at = DateTime.UtcNow.AddHours(1),
        }));
      });
    });

    myUpstreamServer = BuildKestrel(router => router.MapGet("{*path}", (req, res, data) =>
    {
      myUpstreamAuthByPath[(string)data.Values["path"]!] = req.Headers.Authorization.ToString();
      return res.WriteAsync("artifact-body");
    }));
  }

  [Fact]
  public async Task Installation_Token_Is_Used_As_Bearer_And_Cached()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintInboundToken());

    var first = await client.GetAsync("/github/one.txt");
    var second = await client.GetAsync("/github/two.txt");

    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    Assert.Equal(HttpStatusCode.OK, second.StatusCode);

    // The upstream received the installation token as a Bearer token.
    Assert.Equal($"Bearer {InstallationToken}", myUpstreamAuthByPath["repo/one.txt"]);
    Assert.Equal($"Bearer {InstallationToken}", myUpstreamAuthByPath["repo/two.txt"]);

    // The installation token is cached, so the access-token (and installation list) endpoints are hit once
    // across two artifact fetches.
    Assert.Equal(1, myAccessTokenRequests);
    Assert.Equal(1, myListInstallationsRequests);

    // The installation id was auto-resolved from /app/installations.
    Assert.Equal(InstallationId.ToString(), myAccessTokenInstallationId);

    // GitHub was presented a Bearer app JWT signed by the app key with iss = client id.
    Assert.StartsWith("Bearer ", myAccessTokenAuth);
    AssertValidAppJwt(myAccessTokenAuth["Bearer ".Length..]);
  }

  public async Task InitializeAsync()
  {
    myTempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(myTempDirectory);

    await myGitHubApi.StartAsync();
    await myUpstreamServer.StartAsync();

    var upstreamUrl = UrlOf(myUpstreamServer);
    var config = new CachingProxyConfig
    {
      LocalCachePath = myTempDirectory,
      MinimumFreeDiskSpaceMb = 2,
      Prefixes = [$"/github={upstreamUrl}repo"],
      UpstreamAuth =
      {
        ["test"] = new UpstreamAuth
        {
          UrlPrefixes = [new Uri(upstreamUrl, "repo/").GetHostPortPath()],
          ClientId = AppClientId,
          PrivateKey = myAppRsa.ExportRSAPrivateKeyPem(),
          GitHubApiBaseUrl = UrlOf(myGitHubApi).AbsoluteUri.TrimEnd('/'),
          // InstallationId intentionally omitted to exercise auto-resolution.
        },
      },
      InboundAuth = new CachingProxyConfig.InboundAuthConfig
      {
        Issuer = Issuer,
        Audience = Audience,
        JwksUrl = new Uri(UrlOf(myGitHubApi), "jwks.json"),
      },
    };

    myProxyHost = new HostBuilder()
      .ConfigureWebHost(webHostBuilder => webHostBuilder
        .UseTestServer()
        .ConfigureAppConfiguration(cfg =>
          cfg.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(config))))
        .ConfigureServices((context, services) => services
          .AddSingleton(config)
          .ConfigureOurServices(context.Configuration))
        .Configure((context, builder) => builder.ConfigureOurApp(context.Configuration)))
      .Build();
    await myProxyHost.StartAsync();
  }

  public async Task DisposeAsync()
  {
    if (myProxyHost != null) await myProxyHost.StopAsync();
    myProxyHost?.Dispose();
    await myUpstreamServer.StopAsync();
    await myGitHubApi.StopAsync();
    myAppRsa.Dispose();
    myInboundRsa.Dispose();
    try { Directory.Delete(myTempDirectory, recursive: true); } catch { /* best effort */ }
  }

  private string MintInboundToken()
  {
    var key = new RsaSecurityKey(myInboundRsa) { KeyId = Kid };
    var token = new JwtSecurityToken(
      issuer: Issuer,
      audience: Audience,
      expires: DateTime.UtcNow.AddMinutes(5),
      signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
    return new JwtSecurityTokenHandler().WriteToken(token);
  }

  private void AssertValidAppJwt(string jwt)
  {
    new JwtSecurityTokenHandler().ValidateToken(jwt, new TokenValidationParameters
    {
      ValidateIssuer = true,
      ValidIssuer = AppClientId,
      ValidateAudience = false,
      ValidateIssuerSigningKey = true,
      IssuerSigningKey = new RsaSecurityKey(myAppRsa),
      ValidateLifetime = true,
    }, out _);
  }

  // Minimal JWKS document publishing the RSA public key under our Kid.
  private static string JwksJson(RSA rsa)
  {
    var p = rsa.ExportParameters(includePrivateParameters: false);
    return JsonSerializer.Serialize(new
    {
      keys = new[]
      {
        new
        {
          kty = "RSA",
          use = "sig",
          kid = Kid,
          alg = "RS256",
          n = Base64UrlEncoder.Encode(p.Modulus),
          e = Base64UrlEncoder.Encode(p.Exponent),
        },
      },
    });
  }

  private static WebApplication BuildKestrel(Action<IRouteBuilder> configure)
  {
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
    var app = builder.Build();
    app.UseRouter(configure);
    return app;
  }

  private static Uri UrlOf(WebApplication app) =>
    new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
}
