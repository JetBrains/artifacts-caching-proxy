using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
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

// End-to-end coverage for inbound JWT bearer validation backed by a JWKS endpoint. A prefix whose
// upstream requires auth (matching UpstreamAuth) requires a validated client JWT; a prefix with a
// public upstream stays public, as does /health. The proxy fetches the RSA signing key from a real
// JWKS server. Asserts: valid token -> proxied, missing/wrong-key token -> 401, public prefix and
// /health -> served without a token, and that a matched-upstream prefix with no InboundAuth returns 401
// (fails closed via the deny scheme).
public class InboundAuthTest : IAsyncLifetime
{
  private const string ClientId = "svc-proxy";
  private const string ClientSecret = "s3cr3t";
  private const string AccessToken = "issued-access-token";
  private const string PrivateArtifact = "private-artifact-body";

  // What client-credentials mode puts on the wire: the client id as the Basic username and the issued
  // access token as the password (see UpstreamAuthorizationProvider). The private upstream route below
  // demands it, so an anonymous reader that ever sees PrivateArtifact got it from our cache, not upstream.
  private static readonly string ourUpstreamCredential =
    "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{AccessToken}"));

  private const string Issuer = "https://issuer.example.com";
  private const string Audience = "artifacts-caching-proxy";
  private const string Kid = "test-key-1";

  // The proxy's signing key, published via the auth server's JWKS endpoint and used to mint valid tokens.
  private readonly RSA myRsa = RSA.Create(2048);

  // One server modelling the OAuth identity provider: it serves both the JWKS (inbound validation) and
  // the token endpoint (outbound upstream auth), as a real provider like JetBrains hub does.
  private readonly WebApplication myAuthServer;
  private readonly WebApplication myUpstreamServer;

  // Exists only to prove it is never contacted: the "other host" a remainder carrying an authority of its
  // own would aim the request - and a gated prefix's credential - at (MRI-4844). It answers everything, so
  // a regression cannot be mistaken for an unreachable host.
  private readonly WebApplication myCollectorServer;
  private readonly ConcurrentQueue<string> myCollectedRequests = new();

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

    myUpstreamServer = BuildKestrel(router => router
      // Registered before the catch-all every other test uses, so only this one path is credential-gated -
      // a real private repository serves nothing without the account.
      .MapGet("secure/private.jar", (req, res, _) =>
      {
        if (req.Headers.Authorization.ToString() != ourUpstreamCredential)
        {
          res.StatusCode = StatusCodes.Status401Unauthorized;
          return res.WriteAsync("upstream-unauthorized");
        }

        return res.WriteAsync(PrivateArtifact);
      })
      .MapGet("{*path}", (_, res, _) => res.WriteAsync("artifact-body")));

    myCollectorServer = BuildKestrel(router => router.MapGet("{*path}", (req, res, _) =>
    {
      myCollectedRequests.Enqueue($"{req.Path} {req.Headers.Authorization}");
      return res.WriteAsync("collected");
    }));
  }

  [Fact]
  public async Task Protected_Prefix_Without_Token_Is_Unauthorized()
  {
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Protected_Prefix_Without_Token_Challenges_Basic_Only()
  {
    // Basic-only clients (Maven/Gradle/npm) need a Basic challenge to prompt for / send the JWT as the
    // password. Bearer is deliberately not advertised even though a Bearer token is still accepted: an
    // OCI client reads a Bearer challenge as "fetch a token from realm", and the realm here is an
    // application name rather than a token endpoint, so it would abort instead of falling back to Basic.
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/private/one.jar");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    var basic = Assert.Single(response.Headers.WwwAuthenticate);
    Assert.Equal("Basic", basic.Scheme);
    Assert.Equal("realm=\"CachingProxyTests\"", basic.Parameter);
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
  public async Task Request_Metric_Reports_Whether_The_Caller_Was_Authenticated()
  {
    // The same predicate that picks Cache-Control private over public, so this pins the pair together: a
    // response marked private must never be counted as anonymous traffic. Note the 401 cases above produce no
    // measurement at all - UseAuthorization short-circuits them before either storage middleware runs, so
    // they never reach SetStatusHeader.
    using var metrics = new RequestMetricRecorder(myProxyHost!);

    using var authorized = myProxyHost!.GetTestServer().CreateClient();
    authorized.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());
    Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync("/private/one.jar")).StatusCode);

    using var anonymous = myProxyHost!.GetTestServer().CreateClient();
    Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/public/plain.jar")).StatusCode);

    Assert.Equal(["true", "false"], metrics.TagValues("authenticated"));
    // This deployment declares no caching profiles, so both fall to the sentinel.
    Assert.Equal(["none", "none"], metrics.TagValues("profile"));
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
  public async Task A_Public_Prefix_Cannot_Read_A_Private_Prefixs_Cache()
  {
    // MRI-4842. /public and /private sit on one host, differing only in their configured subpath, and the
    // cache key follows the resolved upstream while the gate follows the prefix. So "/public//secure/..."
    // - whose "/secure/..." remainder replaces /public's base path - used to hit the entry /private had
    // filled and serve a private artifact to a caller with no token at all.
    using var authorized = myProxyHost!.GetTestServer().CreateClient();
    authorized.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());

    var warm = await authorized.GetAsync("/private/private.jar");
    Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
    Assert.Equal(PrivateArtifact, await warm.Content.ReadAsStringAsync());

    // The artifact is now cached, so the attack below has something to read - without this the test could
    // pass on an empty cache and prove nothing.
    var cached = await authorized.GetAsync("/private/private.jar");
    Assert.Equal(CachingProxyStatus.HIT.ToString(),
      cached.Headers.GetValues(CachingProxyConstants.StatusHeader).Single());

    using var anonymous = myProxyHost!.GetTestServer().CreateClient();

    var attack = await anonymous.GetAsync("/public//secure/private.jar");

    Assert.Equal(HttpStatusCode.BadRequest, attack.StatusCode);
    Assert.DoesNotContain(PrivateArtifact, await attack.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task A_Gated_Prefix_Cannot_Be_Aimed_At_Another_Host()
  {
    // MRI-4844. A remainder starting with "//" replaces the authority, so the request - and with it the
    // credential /private is configured to send - went to a host the caller named. The valid token here only
    // gets past [Authorize]; it is the proxy's own upstream account that would have leaked.
    using var client = myProxyHost!.GetTestServer().CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());

    var response = await client.GetAsync($"/private///{UrlOf(myCollectorServer).Authority}/steal");

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Empty(myCollectedRequests);
  }

  [Fact]
  public async Task A_Public_Prefix_Cannot_Be_Aimed_At_Another_Host()
  {
    // The same SSRF with no credential to leak and no token to present: an unauthenticated caller must not
    // be able to make the proxy fetch (and cache) a URL of their choosing.
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync($"/public///{UrlOf(myCollectorServer).Authority}/steal");

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Empty(myCollectedRequests);
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
  public async Task Matched_Upstream_Without_InboundAuth_Is_Unauthorized()
  {
    // A matched-upstream prefix carries [Authorize], but no inbound JWT scheme is configured. Rather than
    // throwing a 500 (no authentication scheme) or serving it unauthenticated, the request must fail
    // closed with 401 via the deny scheme registered when InboundAuth is absent.
    var upstreamUrl = UrlOf(myUpstreamServer);
    var config = new CachingProxyConfig
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
          ClientId = ClientId,
          ClientSecret = ClientSecret,
        },
      },
      // InboundAuth deliberately left null.
    };

    using var host = BuildProxyHost(config);
    await host.StartAsync();
    try
    {
      using var client = host.GetTestServer().CreateClient();

      var response = await client.GetAsync("/private/one.jar");

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    finally
    {
      await host.StopAsync();
    }
  }

  [Fact]
  public async Task Matched_Upstream_Without_InboundAuth_Challenges_Basic()
  {
    // The deny scheme (no InboundAuth configured) has no Bearer scheme, so it advertises Basic only.
    var upstreamUrl = UrlOf(myUpstreamServer);
    var config = new CachingProxyConfig
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
          ClientId = ClientId,
          ClientSecret = ClientSecret,
        },
      },
      // InboundAuth deliberately left null -> deny scheme.
    };

    using var host = BuildProxyHost(config);
    await host.StartAsync();
    try
    {
      using var client = host.GetTestServer().CreateClient();

      var response = await client.GetAsync("/private/one.jar");

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    finally
    {
      await host.StopAsync();
    }
  }

  [Fact]
  public async Task Oci_Probe_Is_Challenged_When_An_Oci_Prefix_Is_Gated()
  {
    // A registry client fixes its auth strategy for the whole host from GET /v2/ alone: answer it 200 with
    // no challenge and the client concludes the registry is anonymous, never sends the `docker login`
    // credentials, and treats the 401 on the manifest that follows as terminal. So the probe itself has to
    // challenge once a gated OCI prefix exists.
    using var host = BuildProxyHost(BuildOciConfig());
    await host.StartAsync();
    try
    {
      using var client = host.GetTestServer().CreateClient();

      var response = await client.GetAsync("/v2/");

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
      var basic = Assert.Single(response.Headers.WwwAuthenticate);
      Assert.Equal("Basic", basic.Scheme);
      // The version header travels on the 401 too, as it does from a real registry.
      Assert.Equal(CachingProxyConstants.DockerApiVersion,
        response.Headers.GetValues(CachingProxyConstants.DockerApiVersionHeader).Single());
    }
    finally
    {
      await host.StopAsync();
    }
  }

  [Fact]
  public async Task Oci_Probe_With_Valid_Token_Is_Answered()
  {
    using var host = BuildProxyHost(BuildOciConfig());
    await host.StartAsync();
    try
    {
      using var client = host.GetTestServer().CreateClient();
      client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());

      var response = await client.GetAsync("/v2/");

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      Assert.Equal("{}", await response.Content.ReadAsStringAsync());
    }
    finally
    {
      await host.StopAsync();
    }
  }

  [Fact]
  public async Task Oci_Probe_Stays_Public_Without_A_Gated_Oci_Prefix()
  {
    // The shared host gates /private, but that is not an OCI prefix, so nothing about the registry probe
    // changes: a deployment whose OCI prefixes are all public keeps serving anonymous pulls.
    using var client = myProxyHost!.GetTestServer().CreateClient();

    var response = await client.GetAsync("/v2/");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("{}", await response.Content.ReadAsStringAsync());
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
    await myCollectorServer.StartAsync();

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
      {
        ["test"] = new UpstreamAuth
        {
          // Scoped to the /secure subtree so the /public prefix (same host) is unauthenticated.
          UrlPrefixes = [new Uri(upstreamUrl, "secure/").GetHostPortPath()],
          TokenEndpoint = new Uri(UrlOf(myAuthServer), "token"),
          ClientId = ClientId,
          ClientSecret = ClientSecret,
        },
      },
      InboundAuth = new CachingProxyConfig.InboundAuthConfig
      {
        Issuer = Issuer,
        Audiences = [Audience],
        JwksUrl = new Uri(UrlOf(myAuthServer), "jwks.json"),
        RequireExpiration = requireExpiration,
      },
    };
  }

  // A gated OCI prefix: an Oci profile on a prefix whose upstream matches an UpstreamAuth entry, so the
  // prefix carries [Authorize] and the registry probe has to challenge.
  private CachingProxyConfig BuildOciConfig()
  {
    var upstreamUrl = UrlOf(myUpstreamServer);
    return new CachingProxyConfig
    {
      LocalCachePath = myTempDirectory,
      MinimumFreeDiskSpaceMb = 2,
      CachingProfiles = new Dictionary<string, CachingProfile> { ["docker"] = new() { Oci = true } },
      Prefixes = [new CachingProxyPrefix($"/v2/private-hub={upstreamUrl}secure/v2", Profile: "docker")],
      UpstreamAuth =
      {
        ["test"] = new UpstreamAuth
        {
          UrlPrefixes = [new Uri(upstreamUrl, "secure/").GetHostPortPath()],
          TokenEndpoint = new Uri(UrlOf(myAuthServer), "token"),
          ClientId = ClientId,
          ClientSecret = ClientSecret,
        },
      },
      InboundAuth = new CachingProxyConfig.InboundAuthConfig
      {
        Issuer = Issuer,
        Audiences = [Audience],
        JwksUrl = new Uri(UrlOf(myAuthServer), "jwks.json"),
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
    await myCollectorServer.StopAsync();
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
