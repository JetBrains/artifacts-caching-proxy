using System;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

// End-to-end coverage for inbound JWT bearer validation backed by a JWKS endpoint. A prefix whose
// upstream requires auth (matching UpstreamAuth) requires a validated client JWT; a prefix with a
// public upstream stays public, as does /health. The proxy fetches the RSA signing key from a real
// JWKS server. Asserts: valid token -> proxied, missing/wrong-key token -> 401, public prefix and
// /health -> served without a token, and that a matched-upstream config with no InboundAuth fails to start.
public class InboundAuthTest : IAsyncLifetime
{
  private const string ClientId = "svc-proxy";
  private const string ClientSecret = "s3cr3t";
  private const string AccessToken = "issued-access-token";

  private const string Issuer = "https://issuer.example.com";
  private const string Audience = "artifacts-caching-proxy";
  private const string Kid = "test-key-1";

  // The proxy's signing key, published via the auth server's JWKS endpoint and used to mint valid tokens.
  private readonly RSA myRsa = RSA.Create(2048);

  // One server modelling the OAuth identity provider: it serves both the JWKS (inbound validation) and
  // the token endpoint (outbound upstream auth), as a real provider like JetBrains hub does.
  private readonly WebApplication myAuthServer;
  private readonly WebApplication myUpstreamServer;
  private string myTempDirectory = "";
  private IHost? myProxyHost;

  public InboundAuthTest()
  {
    myAuthServer = BuildKestrel(router =>
    {
      router.MapGet("jwks.json", (_, res, _) =>
      {
        res.ContentType = "application/json";
        return res.WriteAsync(JwksJson(myRsa));
      });
      router.MapPost("token", async (_, res, _) =>
      {
        res.ContentType = "application/json";
        await res.WriteAsync(JsonSerializer.Serialize(new
        {
          access_token = AccessToken,
          token_type = "Bearer",
          expires_in = 3600,
        }));
      });
    });

    myUpstreamServer = BuildKestrel(router => router.MapGet("{*path}", (_, res, _) =>
      res.WriteAsync("artifact-body")));
  }

  [Fact]
  public async Task Protected_Prefix_Without_Token_Is_Unauthorized()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Protected_Prefix_With_Valid_Token_Is_Served()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("artifact-body", await response.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task Authorized_Response_Is_Cache_Control_Private()
  {
    // An authenticated request is served only to the requesting client, so it must not be
    // stored by shared/intermediary caches.
    using var client = myProxyHost!.GetTestServer().CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    // HttpResponseHeaders re-serializes the parsed directives, ordering "private" after max-age.
    Assert.Equal("max-age=31536000, private", response.Headers.CacheControl?.ToString());
  }

  [Fact]
  public async Task Anonymous_Response_Stays_Cache_Control_Public()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/public/plain.jar");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("public, max-age=31536000", response.Headers.CacheControl?.ToString());
  }

  [Fact]
  public async Task Protected_Prefix_With_Valid_Token_As_Basic_Password_Is_Served()
  {
    // Basic-only clients (Maven/Gradle/npm) carry the JWT as the Basic password; the username is ignored.
    using var client = myProxyHost!.GetTestServer().CreateClient();
    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"any-user:{MintToken()}"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("artifact-body", await response.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task Protected_Prefix_With_Invalid_Basic_Password_Is_Unauthorized()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();
    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("any-user:not-a-jwt"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Protected_Prefix_With_Unknown_Key_Is_Unauthorized()
  {
    // Signed with a key that is not published in the JWKS, so signature validation must fail.
    using var otherRsa = RSA.Create(2048);
    using var client = myProxyHost!.GetTestServer().CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(otherRsa));

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Token_Without_Expiration_Is_Unauthorized_By_Default()
  {
    // The shared host uses the default RequireExpiration = true, so a token with no exp claim
    // (e.g. a JetBrains hub permanent token) is rejected.
    using var client = myProxyHost!.GetTestServer().CreateClient();
    client.DefaultRequestHeaders.Authorization =
      new AuthenticationHeaderValue("Bearer", MintToken(withExpiration: false));

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Token_Without_Expiration_Is_Served_When_Not_Required()
  {
    using var host = BuildProxyHost(BuildConfig(requireExpiration: false));
    await host.StartAsync();
    try
    {
      using var client = host.GetTestServer().CreateClient();
      client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", MintToken(withExpiration: false));

      var response = await client.GetAsync("/private/one.jar");

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    finally
    {
      await host.StopAsync();
    }
  }

  [Fact]
  public async Task Public_Prefix_Is_Served_Without_Token()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/public/plain.jar");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }

  [Fact]
  public async Task Health_Is_Public()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/health");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }

  [Fact]
  public void Matched_Upstream_Without_InboundAuth_Throws()
  {
    var upstreamUrl = UrlOf(myUpstreamServer);
    var config = new CachingProxyConfig
    {
      Prefixes = [$"/private={upstreamUrl}secure"],
      UpstreamAuth =
      [
        new UpstreamAuth
        {
          UrlPrefix = new Uri(upstreamUrl, "secure/").AbsoluteUri,
          TokenEndpoint = new Uri(UrlOf(myAuthServer), "token"),
          ClientId = ClientId,
          ClientSecret = ClientSecret,
        },
      ],
      // InboundAuth deliberately left null.
    };

    Assert.Throws<ArgumentException>(() => new RemoteServers(config));
  }

  [Fact]
  public void Credential_Less_Upstream_Without_InboundAuth_Does_Not_Throw()
  {
    // A credential-less UpstreamAuth (redirect-only / external auth, e.g. CloudFront) does NOT make the
    // prefix require inbound auth, so a config with no InboundAuth must construct fine — contrast with
    // Matched_Upstream_Without_InboundAuth_Throws, whose entry carries a ClientId.
    var upstreamUrl = UrlOf(myUpstreamServer);
    var config = new CachingProxyConfig
    {
      Prefixes = [$"/cdn={upstreamUrl}cdn"],
      UpstreamAuth =
      [
        new UpstreamAuth { UrlPrefix = new Uri(upstreamUrl, "cdn/").AbsoluteUri },
      ],
      // InboundAuth deliberately left null.
    };

    var servers = new RemoteServers(config);
    Assert.Single(servers.Endpoints);
  }

  private string MintToken(RSA? rsa = null, bool withExpiration = true)
  {
    var key = new RsaSecurityKey(rsa ?? myRsa) { KeyId = Kid };
    var token = new JwtSecurityToken(
      issuer: Issuer,
      audience: Audience,
      claims: null,
      notBefore: null,
      expires: withExpiration ? DateTime.UtcNow.AddMinutes(5) : null,
      signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
    return new JwtSecurityTokenHandler().WriteToken(token);
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

  public async Task InitializeAsync()
  {
    myTempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(myTempDirectory);

    await myAuthServer.StartAsync();
    await myUpstreamServer.StartAsync();

    myProxyHost = BuildProxyHost(BuildConfig());
    await myProxyHost.StartAsync();
  }

  private CachingProxyConfig BuildConfig(bool requireExpiration = true)
  {
    var upstreamUrl = UrlOf(myUpstreamServer);
    return new CachingProxyConfig
    {
      LocalCachePath = myTempDirectory,
      MinimumFreeDiskSpaceMb = 2,
      Prefixes =
      [
        $"/private={upstreamUrl}secure",
        $"/public={upstreamUrl}open",
      ],
      UpstreamAuth =
      [
        new UpstreamAuth
        {
          // Scoped to the /secure subtree so the /public prefix (same host) is unauthenticated.
          UrlPrefix = new Uri(upstreamUrl, "secure/").AbsoluteUri,
          TokenEndpoint = new Uri(UrlOf(myAuthServer), "token"),
          ClientId = ClientId,
          ClientSecret = ClientSecret,
        },
      ],
      InboundAuth = new CachingProxyConfig.InboundAuthConfig
      {
        Issuer = Issuer,
        Audience = Audience,
        JwksUrl = new Uri(UrlOf(myAuthServer), "jwks.json"),
        RequireExpiration = requireExpiration,
      },
    };
  }

  private static IHost BuildProxyHost(CachingProxyConfig config) =>
    new HostBuilder()
      .ConfigureWebHost(webHostBuilder => webHostBuilder
        .UseTestServer()
        .ConfigureAppConfiguration(cfg =>
          cfg.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(config))))
        .ConfigureServices((context, services) => services
          .AddSingleton(config)
          .ConfigureOurServices(context.Configuration))
        .Configure((context, builder) => builder.ConfigureOurApp(context.Configuration)))
      .Build();

  public async Task DisposeAsync()
  {
    if (myProxyHost != null) await myProxyHost.StopAsync();
    myProxyHost?.Dispose();
    await myUpstreamServer.StopAsync();
    await myAuthServer.StopAsync();
    myRsa.Dispose();
    try { Directory.Delete(myTempDirectory, recursive: true); } catch { /* best effort */ }
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
