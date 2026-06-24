using System;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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

// End-to-end coverage for per-upstream OAuth client-credentials auth: a real token endpoint and a
// real upstream, both recording the Authorization header they receive, so we can assert the proxy
// (1) authenticates the token request with Basic clientId:clientSecret, and (2) sends the upstream
// Basic clientId:access_token. Also asserts the token is cached (token endpoint hit once for two
// artifact fetches) and that unauthenticated upstreams still send no Authorization.
public class UpstreamAuthTest : IAsyncLifetime
{
  private const string ClientId = "svc-proxy";
  private const string ClientSecret = "s3cr3t";
  private const string AccessToken = "issued-access-token";

  // The /private prefix has a matching UpstreamAuth, so it now also requires a validated inbound JWT,
  // whose RSA signing key is published by the auth server's JWKS endpoint below.
  private const string Issuer = "https://issuer.example.com";
  private const string Audience = "artifacts-caching-proxy";
  private const string Kid = "test-key-1";
  private readonly RSA myRsa = RSA.Create(2048);

  // One server modelling the OAuth identity provider: it serves both the token endpoint (outbound
  // upstream auth) and the JWKS (inbound validation), as a real provider like JetBrains hub does.
  private readonly WebApplication myAuthServer;
  private readonly WebApplication myUpstreamServer;
  private string myTempDirectory = "";
  private IHost? myProxyHost;

  private int myTokenRequests;
  private string myTokenAuthHeader = "";
  private string myTokenGrantType = "";
  private readonly ConcurrentDictionary<string, string> myUpstreamAuthByPath = new();

  public UpstreamAuthTest()
  {
    myAuthServer = BuildKestrel(router =>
    {
      router.MapGet("jwks.json", (_, res, _) =>
      {
        res.ContentType = "application/json";
        return res.WriteAsync(JwksJson(myRsa));
      });
      router.MapPost("token", async (req, res, _) =>
      {
        Interlocked.Increment(ref myTokenRequests);
        myTokenAuthHeader = req.Headers.Authorization.ToString();
        var form = await req.ReadFormAsync();
        myTokenGrantType = form["grant_type"].ToString();

        res.ContentType = "application/json";
        await res.WriteAsync(JsonSerializer.Serialize(new
        {
          access_token = AccessToken,
          token_type = "Bearer",
          expires_in = 3600,
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
  public async Task Token_Is_Used_As_Basic_Password_And_Cached()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());

    var first = await client.GetAsync("/private/one.jar");
    var second = await client.GetAsync("/private/two.jar");

    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    Assert.Equal(HttpStatusCode.OK, second.StatusCode);

    // Upstream received the token as the Basic password, with the client id as the username.
    var expectedUpstream = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{AccessToken}"));
    Assert.Equal(expectedUpstream, myUpstreamAuthByPath["secure/one.jar"]);
    Assert.Equal(expectedUpstream, myUpstreamAuthByPath["secure/two.jar"]);

    // The token endpoint authenticated the client with Basic clientId:clientSecret via client_credentials.
    var expectedTokenAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
    Assert.Equal(expectedTokenAuth, myTokenAuthHeader);
    Assert.Equal("client_credentials", myTokenGrantType);

    // Two artifact fetches, but the token is cached so the token endpoint is hit only once.
    Assert.Equal(1, myTokenRequests);
  }

  [Fact]
  public async Task Authenticated_Redirect_Url_Is_Redirected_Not_Cached()
  {
    // maven-metadata.xml matches RedirectToRemoteUrlsRegex. It is mutable, so even for an authed prefix
    // it must be redirected to the origin with 307 rather than proxied/cached: a 307 preserves the method
    // and the client reuses its own credentials for the origin. The proxy must therefore NOT fetch it
    // upstream (caching it would pin a stale copy for a protected source).
    var response = await myProxyHost!.GetTestServer().CreateRequest("/private/maven-metadata.xml")
      .AddHeader("Authorization", "Bearer " + MintToken())
      .GetAsync();

    Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode); // 307, == RedirectKeepVerb
    Assert.Equal(new Uri(UrlOf(myUpstreamServer), "secure/maven-metadata.xml"), response.Headers.Location);

    // It was redirected, not proxied through — the upstream never received the request.
    Assert.False(myUpstreamAuthByPath.ContainsKey("secure/maven-metadata.xml"));
  }

  [Fact]
  public async Task External_Auth_Without_ClientId_Is_Redirected_Without_Inbound_Auth()
  {
    // The /cdn prefix has a matching UpstreamAuth that carries no ClientId (redirect-only / external auth,
    // e.g. CloudFront). The proxy holds no upstream credentials and must not gate the prefix with inbound
    // auth: with NO Authorization header it still returns a 307 to the origin, which authorizes per-user.
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/cdn/asset.bin");

    Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode); // 307, == RedirectKeepVerb
    Assert.Equal(new Uri(UrlOf(myUpstreamServer), "cdn/asset.bin"), response.Headers.Location);

    // It was redirected, not proxied through — the upstream never received the request and no token was minted.
    Assert.False(myUpstreamAuthByPath.ContainsKey("cdn/asset.bin"));
    Assert.Equal(0, myTokenRequests);
  }

  [Fact]
  public async Task Unauthenticated_Upstream_Sends_No_Authorization()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/public/plain.jar");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("", myUpstreamAuthByPath["open/plain.jar"]);
    Assert.Equal(0, myTokenRequests);
  }

  public async Task InitializeAsync()
  {
    myTempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(myTempDirectory);

    await myAuthServer.StartAsync();
    await myUpstreamServer.StartAsync();

    var upstreamUrl = UrlOf(myUpstreamServer);
    var config = new CachingProxyConfig
    {
      LocalCachePath = myTempDirectory,
      MinimumFreeDiskSpaceMb = 2,
      Prefixes =
      [
        $"/private={upstreamUrl}secure",
        $"/public={upstreamUrl}open",
        $"/cdn={upstreamUrl}cdn",
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
          Scope = "read:artifacts",
        },
        new UpstreamAuth
        {
          // Credential-less: redirect-only / external auth (e.g. CloudFront). No ClientId, so the proxy
          // holds no upstream credentials, always redirects, and does not require inbound auth.
          UrlPrefix = new Uri(upstreamUrl, "cdn/").AbsoluteUri,
        },
      ],
      InboundAuth = new CachingProxyConfig.InboundAuthConfig
      {
        Issuer = Issuer,
        Audience = Audience,
        JwksUrl = new Uri(UrlOf(myAuthServer), "jwks.json"),
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
    await myAuthServer.StopAsync();
    myRsa.Dispose();
    try { Directory.Delete(myTempDirectory, recursive: true); } catch { /* best effort */ }
  }

  private string MintToken()
  {
    var key = new RsaSecurityKey(myRsa) { KeyId = Kid };
    var token = new JwtSecurityToken(
      issuer: Issuer,
      audience: Audience,
      expires: DateTime.UtcNow.AddMinutes(5),
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
