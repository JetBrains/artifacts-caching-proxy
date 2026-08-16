using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// End-to-end coverage for a service account on an OCI upstream (a Docker Hub PAT is the real case): a
// registry that challenges every pull, a token endpoint on a *different* host - as Hub's auth.docker.io
// is - and both recording the Authorization they receive. Asserts the PAT reaches the token endpoint only
// when the entry declares that realm, and that the minted token is what goes upstream.
//
// The account also gates these prefixes (see RemoteServers), so every pull below carries an inbound client
// JWT, validated against the JWKS the token server publishes alongside its token endpoint.
public class RegistryServiceAccountTest : IAsyncLifetime
{
  private const string Account = "jetbrains-mirror";
  private const string Pat = "dckr_pat_do_not_log_me";
  private const string IssuedToken = "issued-registry-token";

  // Deliberately shorter than the path being pulled (library/allowed): a registry may scope to a group of
  // repositories, and its answer is the only way to know it does.
  private const string GroupScope = "repository:library:pull";

  private const string Issuer = "https://issuer.example.com";
  private const string Audience = "artifacts-caching-proxy";
  private const string Kid = "test-key-1";

  // Signs the inbound client JWTs; its public half is published as the JWKS the proxy validates against.
  private readonly RSA myRsa = RSA.Create(2048);

  private readonly WebApplication myRegistryServer;
  private readonly WebApplication myTokenServer;
  private string myTempDirectory = "";
  private IHost? myProxyHost;

  // Every Authorization the registry saw, per path and in order, so a 401-then-retry is visible as two
  // entries rather than just the last one.
  private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> myRegistryAuthByPath = new();
  private int myTokenRequests;
  private string myTokenAuthHeader = "";
  private string myTokenScope = "";
  private string myTokenQuery = "";

  // Set by a test to make the token endpoint refuse, with the body a real one sends along with it.
  private (HttpStatusCode Status, string Body)? myTokenFailure;

  private readonly WarningCollector myWarnings = new();

  public RegistryServiceAccountTest()
  {
    myRegistryServer = BuildKestrel(router => router.MapGet("{*path}", (req, res, data) =>
    {
      var path = (string)data.Values["path"]!;
      var authorization = req.Headers.Authorization.ToString();
      myRegistryAuthByPath.GetOrAdd(path, _ => new ConcurrentQueue<string>()).Enqueue(authorization);

      if (authorization != $"Bearer {IssuedToken}")
      {
        // What every mainstream registry answers, anonymous pull or not. The realm is on another host, so
        // whether the account travels there is UpstreamAuth.TokenRealms' decision.
        //
        // Which routes name a scope is the point of the exercise. This one does, and names a group rather
        // than the image, the way Space does; /other/ names none, as some registries do; the ping cannot
        // name one at all. Nothing in a URL says which a registry will do, which is why the scope is
        // learned and not computed.
        res.StatusCode = (int)HttpStatusCode.Unauthorized;
        var scope = path.StartsWith("v2/library/") ? $",scope=\"{GroupScope}\"" : "";
        res.Headers["WWW-Authenticate"] =
          $"Bearer realm=\"{UrlOf(myTokenServer!)}token\",service=\"registry.test\"{scope}";
        return Task.CompletedTask;
      }

      res.ContentType = "application/vnd.oci.image.manifest.v1+json";
      return res.WriteAsync("{\"schemaVersion\":2}");
    }));

    myTokenServer = BuildKestrel(router =>
    {
      router.MapGet("token", (req, res, _) =>
      {
        Interlocked.Increment(ref myTokenRequests);
        myTokenAuthHeader = req.Headers.Authorization.ToString();
        myTokenScope = req.Query["scope"].ToString();
        myTokenQuery = req.QueryString.Value ?? "";

        res.ContentType = "application/json";
        if (myTokenFailure is { } failure)
        {
          res.StatusCode = (int)failure.Status;
          return res.WriteAsync(failure.Body);
        }

        return res.WriteAsync(JsonSerializer.Serialize(new { token = IssuedToken, expires_in = 300 }));
      });
      router.MapGet("jwks.json", (_, res, _) =>
      {
        res.ContentType = "application/json";
        return res.WriteAsync(JwksJson(myRsa));
      });
    });
  }

  [Fact]
  public async Task A_Declared_Realm_Receives_The_Account_And_Its_Token_Goes_Upstream()
  {
    using var client = CreateAuthenticatedClient();

    var first = await client.GetAsync("/v2/allowlisted/library/allowed/manifests/1.0");
    var second = await client.GetAsync("/v2/allowlisted/library/allowed/manifests/2.0");

    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    Assert.Equal(HttpStatusCode.OK, second.StatusCode);

    // The account is presented at the token endpoint, as Basic, and the token is asked for exactly the
    // scope the registry named - not the wider one the path suggests, and not the alias the client used.
    Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Account}:{Pat}")), myTokenAuthHeader);
    Assert.Equal(GroupScope, myTokenScope);

    // One token for both tags: the challenge is remembered whole, scope included, so the second pull mints
    // nothing. Deriving the scope again instead would ask for a different one and pay for a second token.
    Assert.Equal(1, myTokenRequests);

    // The first pull goes out unauthenticated, pays the 401 and retries with the minted token. The PAT is
    // never offered to the registry itself: it has no use for it, and the token endpoint is the only place
    // an account belongs.
    Assert.Equal(["", $"Bearer {IssuedToken}"], AuthSeenAt("v2/library/allowed/manifests/1.0"));
    // Scheme-level, so it holds however the credential is encoded.
    Assert.DoesNotContain("Basic", string.Join(" ", myRegistryAuthByPath.Values.SelectMany(queue => queue)));

    // The second pull pays no 401: the challenge learned from the first is reused to mint up front.
    Assert.Equal([$"Bearer {IssuedToken}"], AuthSeenAt("v2/library/allowed/manifests/2.0"));
  }

  [Fact]
  public async Task An_Undeclared_Realm_Gets_An_Anonymous_Token_Request()
  {
    using var client = CreateAuthenticatedClient();

    var response = await client.GetAsync("/v2/foreign/library/foreign/manifests/1.0");

    // The pull still succeeds - anonymously, which is all a public mirror needs. What must not happen is
    // the account travelling to a token endpoint nobody vetted: the realm is a URL chosen by the very
    // response we are authenticating to.
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(1, myTokenRequests);
    Assert.Equal("", myTokenAuthHeader);
    // This route challenges without a scope, so the path supplies one - the fallback, end to end.
    Assert.Equal("repository:other/library/foreign:pull", myTokenScope);
  }

  [Fact]
  public async Task The_Upstream_Ping_Is_Satisfied_By_An_Unscoped_Token()
  {
    using var client = CreateAuthenticatedClient();

    // The registry's own /v2/ - the base of the mirror, not this proxy's root ping. It names no
    // repository, so there is no scope to state or to derive, and a registry answers it with a token
    // minted for nothing in particular. Refusing to mint one relays the 401 and the mirror looks dead.
    var response = await client.GetAsync("/v2/allowlisted/");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(1, myTokenRequests);
    // Omitted, not sent empty: "scope=" is a different request from no scope at all.
    Assert.DoesNotContain("scope", myTokenQuery);
    Assert.Equal(["", $"Bearer {IssuedToken}"], AuthSeenAt("v2/"));
  }

  [Fact]
  public async Task A_Refused_Token_Request_Reports_What_The_Endpoint_Objected_To()
  {
    // The status on its own is not a diagnosis. Docker Hub answers a username it considers malformed and a
    // scope it does not recognise alike with 400, and the two are a broken secret and a bad request
    // respectively - told apart only by the body, which is therefore what has to reach the log.
    myTokenFailure = (HttpStatusCode.BadRequest, "{\"details\":\"malformed HTTP Authorization header\"}");

    using var client = CreateAuthenticatedClient();
    var response = await client.GetAsync("/v2/allowlisted/library/allowed/manifests/1.0");

    // No token to retry with, so what the client gets is the registry's own 401 - which says nothing about
    // the token endpoint at all. All the evidence there is lives in the log.
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    Assert.Contains(myWarnings.Messages(),
      message => message.Contains("400") && message.Contains("malformed HTTP Authorization header"));
  }

  private string[] AuthSeenAt(string path) => [.. myRegistryAuthByPath[path]];

  // A test client whose every request carries a valid inbound JWT: the account on these upstreams gates
  // their prefixes, so an anonymous request gets 401 before the registry is ever contacted.
  private HttpClient CreateAuthenticatedClient()
  {
    var client = myProxyHost!.GetTestServer().CreateClient();
    var token = new JwtSecurityToken(
      issuer: Issuer,
      audience: Audience,
      claims: null,
      notBefore: null,
      expires: DateTime.UtcNow.AddMinutes(5),
      signingCredentials: new SigningCredentials(
        new RsaSecurityKey(myRsa) { KeyId = Kid }, SecurityAlgorithms.RsaSha256));
    client.DefaultRequestHeaders.Authorization =
      new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    return client;
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

    await myRegistryServer.StartAsync();
    await myTokenServer.StartAsync();

    var registryUrl = UrlOf(myRegistryServer);
    var config = new CachingProxyConfig
    {
      LocalCachePath = myTempDirectory,
      MinimumFreeDiskSpaceMb = 2,
      CachingProfiles = new Dictionary<string, CachingProfile>
      {
        // Only Oci matters here: it is what opts these prefixes into the token dance.
        ["docker"] = new() { Oci = true },
      },
      Prefixes =
      [
        new CachingProxyPrefix($"/v2/allowlisted={registryUrl}v2", Profile: "docker"),
        // A repository group on the same registry - /v2 stays at the root, the group is part of the
        // repository name - so the two prefixes get different UpstreamAuth entries (matched by longest URL
        // prefix) while sharing one challenge.
        new CachingProxyPrefix($"/v2/foreign={registryUrl}v2/other", Profile: "docker"),
      ],
      UpstreamAuth =
      {
        ["allowlisted"] = new UpstreamAuth
        {
          UrlPrefixes = [new Uri(registryUrl, "v2/").GetHostPortPath()],
          Username = Account,
          Password = Pat,
          TokenRealms = [UrlOf(myTokenServer).AbsoluteUri],
        },
        ["foreign"] = new UpstreamAuth
        {
          UrlPrefixes = [new Uri(registryUrl, "v2/other/").GetHostPortPath()],
          Username = Account,
          Password = Pat,
        },
      },
      InboundAuth = new CachingProxyConfig.InboundAuthConfig
      {
        Issuer = Issuer,
        Audiences = [Audience],
        JwksUrl = new Uri(UrlOf(myTokenServer), "jwks.json"),
      },
    };

    myProxyHost = new HostBuilder()
      .ConfigureWebHost(webHostBuilder => webHostBuilder
        .UseTestServer()
        .ConfigureAppConfiguration(cfg =>
          cfg.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(config))))
        .ConfigureOurServices()
        .ConfigureServices(services => services
          .AddSingleton(config)
          .AddSingleton<ILoggerProvider>(myWarnings))
        .Configure((context, builder) => builder.ConfigureOurApp(context.Configuration)))
      .Build();
    await myProxyHost.StartAsync();
  }

  public async Task DisposeAsync()
  {
    if (myProxyHost != null) await myProxyHost.StopAsync();
    myProxyHost?.Dispose();
    await myTokenServer.StopAsync();
    await myRegistryServer.StopAsync();
    myRsa.Dispose();
    try { Directory.Delete(myTempDirectory, recursive: true); } catch { /* best effort */ }
  }

  // Everything the proxy logged at Warning or above, formatted. A failed mint is handled - the caller
  // relays the registry's 401 - so the log is the only place its reason is ever stated.
  private sealed class WarningCollector : ILoggerProvider
  {
    private readonly ConcurrentQueue<string> myMessages = new();

    public string[] Messages() => [.. myMessages];
    public ILogger CreateLogger(string categoryName) => new Sink(myMessages);
    public void Dispose() {}

    private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
    {
      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
      public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
      {
        if (IsEnabled(logLevel)) messages.Enqueue(formatter(state, exception));
      }
    }

    private sealed class NullScope : IDisposable
    {
      public static readonly NullScope Instance = new();
      public void Dispose() {}
    }
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
