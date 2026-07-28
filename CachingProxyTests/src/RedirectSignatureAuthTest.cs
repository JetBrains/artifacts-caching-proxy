using System;
using System.IO;
using System.Linq;
using System.Net;
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

// End-to-end coverage for HMAC redirect-signature validation: a cache-redirector signs a 307 to this
// proxy with cr_exp/cr_sig query parameters (the client JWT is dropped on the cross-host hop), and the
// proxy must accept that signature as sufficient authorization for a private prefix. The signing here
// is a faithful C# re-implementation of the redirector's Lua auth.lua, so these tests double as an
// interop contract. Asserts: valid signature -> proxied (and Cache-Control: private), and every failure
// mode (no signature, tampered path, wrong key, expired) -> 401. Also asserts the JWT path still works
// when a signature key is configured, and that signatures are inert when it is not.
public class RedirectSignatureAuthTest : IAsyncLifetime
{
  private const string Issuer = "https://issuer.example.com";
  private const string Audience = "artifacts-caching-proxy";
  private const string SigningKey = "super-secret-shared-hmac-key-32bytes";

  private readonly RSA myRsa = RSA.Create(2048);
  private const string Kid = "test-key-1";

  private readonly WebApplication myAuthServer;
  private readonly WebApplication myUpstreamServer;
  private string myTempDirectory = "";
  private IHost? myProxyHost;

  public RedirectSignatureAuthTest()
  {
    myAuthServer = BuildKestrel(router =>
    {
      router.MapGet("jwks.json", (_, res, _) =>
      {
        res.ContentType = "application/json";
        return res.WriteAsync(JwksJson(myRsa));
      });
      // The /private upstream requires auth, so proxying it needs an upstream access token from here.
      router.MapPost("token", (_, res, _) =>
      {
        res.ContentType = "application/json";
        return res.WriteAsync(JsonSerializer.Serialize(new
        {
          access_token = "issued-access-token", token_type = "Bearer", expires_in = 3600,
        }));
      });
    });

    myUpstreamServer = BuildKestrel(router => router.MapGet("{*path}", (_, res, _) =>
      res.WriteAsync("artifact-body")));
  }

  [Fact]
  public async Task Valid_Signature_Is_Served()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync(Sign("/private/one.jar"));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("artifact-body", await response.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task Valid_Signature_With_Existing_Query_Is_Served()
  {
    // path_and_query includes a pre-existing query string; cr_exp/cr_sig are appended after it and must
    // be stripped back off in the exact same way to recompute the signature.
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync(Sign("/private/one.jar?classifier=sources&ext=jar"));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }

  [Fact]
  public async Task Valid_Signature_Response_Is_Cache_Control_Private()
  {
    // A signature authorizes exactly one client's redirect, so the artifact must not be stored by
    // shared caches — same treatment as a JWT-authenticated request.
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync(Sign("/private/one.jar"));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("max-age=31536000, private", response.Headers.CacheControl?.ToString());
  }

  [Fact]
  public async Task No_Signature_Is_Unauthorized()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Tampered_Path_Is_Unauthorized()
  {
    // Sign one path, then request a different one with the same cr_exp/cr_sig: the recomputed HMAC over
    // the actual request line must not match.
    using var client = myProxyHost!.GetTestServer().CreateClient();
    var signed = Sign("/private/one.jar");
    var tampered = signed.Replace("/private/one.jar", "/private/evil.jar");

    var response = await client.GetAsync(tampered);

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Wrong_Key_Is_Unauthorized()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync(Sign("/private/one.jar", key: "the-wrong-key"));

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Expired_Signature_Is_Unauthorized()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    // Expired well beyond the default 30s clock-skew tolerance.
    var response = await client.GetAsync(Sign("/private/one.jar", expiry: DateTimeOffset.UtcNow.AddMinutes(-5)));

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Excessively_Long_Lived_Signature_Is_Unauthorized()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync(Sign("/private/one.jar", expiry: DateTimeOffset.UtcNow.AddDays(1)));

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Garbage_Signature_Is_Unauthorized()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();
    var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

    var response = await client.GetAsync($"/private/one.jar?cr_exp={exp}&cr_sig=not-a-real-signature");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Out_Of_Range_Expiry_Is_Unauthorized_Not_Server_Error()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync($"/private/one.jar?cr_exp={long.MaxValue}&cr_sig=AA");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Jwt_Still_Works_When_Signature_Is_Configured()
  {
    // A request without cr_sig forwards to the JwtBearer scheme, so direct clients presenting a JWT keep
    // working even though redirect-signature validation is enabled.
    using var client = myProxyHost!.GetTestServer().CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }

  [Fact]
  public async Task Signature_Is_Inert_When_Not_Configured()
  {
    // Without RedirectSignature configured, cr_sig is meaningless: JwtBearer is the sole scheme and a
    // signed-but-tokenless request fails closed.
    using var host = BuildProxyHost(BuildConfig(withSignature: false));
    await host.StartAsync();
    try
    {
      using var client = host.GetTestServer().CreateClient();

      var response = await client.GetAsync(Sign("/private/one.jar"));

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    finally
    {
      await host.StopAsync();
    }
  }

  // Faithful C# port of the redirector's auth.lua signing: sig = base64url(HMAC-SHA256(key,
  // path_and_query + "\n" + exp)), URL-safe base64 without padding, cr_exp/cr_sig appended last.
  private static string Sign(string pathAndQuery, string key = SigningKey, DateTimeOffset? expiry = null)
  {
    var exp = (expiry ?? DateTimeOffset.UtcNow.AddMinutes(5)).ToUnixTimeSeconds();
    var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{pathAndQuery}\n{exp}"));
    var sig = Base64UrlEncoder.Encode(mac);
    var separator = pathAndQuery.Contains('?') ? '&' : '?';
    return $"{pathAndQuery}{separator}cr_exp={exp}&cr_sig={sig}";
  }

  private string MintToken()
  {
    var key = new RsaSecurityKey(myRsa) { KeyId = Kid };
    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
      issuer: Issuer, audience: Audience, claims: null, notBefore: null,
      expires: DateTime.UtcNow.AddMinutes(5),
      signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
    return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
  }

  private static string JwksJson(RSA rsa)
  {
    var p = rsa.ExportParameters(includePrivateParameters: false);
    return JsonSerializer.Serialize(new
    {
      keys = new[]
      {
        new { kty = "RSA", use = "sig", kid = Kid, alg = "RS256", n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) },
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

  private CachingProxyConfig BuildConfig(bool withSignature = true)
  {
    var upstreamUrl = UrlOf(myUpstreamServer);
    return new CachingProxyConfig
    {
      LocalCachePath = myTempDirectory,
      MinimumFreeDiskSpaceMb = 2,
      Prefixes = [$"/private={upstreamUrl}secure"],
      UpstreamAuth =
      {
        ["test"] = new UpstreamAuth
        {
          UrlPrefixes = [new Uri(upstreamUrl, "secure/").GetHostPortPath()],
          TokenEndpoint = new Uri(UrlOf(myAuthServer), "token"),
          ClientId = "svc-proxy",
          ClientSecret = "s3cr3t",
        },
      },
      InboundAuth = new CachingProxyConfig.InboundAuthConfig
      {
        Issuer = Issuer,
        Audiences = [Audience],
        JwksUrl = new Uri(UrlOf(myAuthServer), "jwks.json"),
        RedirectSignature = withSignature
          ? new CachingProxyConfig.RedirectSignatureConfig { Key = SigningKey }
          : null,
      },
    };
  }

  private static IHost BuildProxyHost(CachingProxyConfig config) =>
    new HostBuilder()
      .ConfigureWebHost(webHostBuilder => webHostBuilder
        .UseTestServer()
        .ConfigureAppConfiguration(cfg =>
          cfg.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(config))))
        .ConfigureOurServices()
        .ConfigureServices(services => services.AddSingleton(config))
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
