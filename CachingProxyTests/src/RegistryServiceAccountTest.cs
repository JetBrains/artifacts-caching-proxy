using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// End-to-end coverage for a service account on an OCI upstream (a Docker Hub PAT is the real case): a
// registry that challenges every pull, a token endpoint on a *different* host - as Hub's auth.docker.io
// is - and both recording the Authorization they receive. Asserts the PAT reaches the token endpoint only
// when the entry declares that realm, that the minted token is what goes upstream, and that a rate-limit
// account leaves the prefix open to anonymous clients.
public class RegistryServiceAccountTest : IAsyncLifetime
{
  private const string Account = "jetbrains-mirror";
  private const string Pat = "dckr_pat_do_not_log_me";
  private const string IssuedToken = "issued-registry-token";

  // Deliberately shorter than the path being pulled (library/allowed): a registry may scope to a group of
  // repositories, and its answer is the only way to know it does.
  private const string GroupScope = "repository:library:pull";

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
        // The two routes differ in one respect on purpose: this one names a scope, and names a group
        // rather than the image, the way Space does; the /other/ one below names none. Nothing in a URL
        // says which a registry will do, which is the whole reason the scope is learned and not computed.
        res.StatusCode = (int)HttpStatusCode.Unauthorized;
        var scope = path.StartsWith("v2/other/") ? "" : $",scope=\"{GroupScope}\"";
        res.Headers["WWW-Authenticate"] =
          $"Bearer realm=\"{UrlOf(myTokenServer!)}token\",service=\"registry.test\"{scope}";
        return Task.CompletedTask;
      }

      res.ContentType = "application/vnd.oci.image.manifest.v1+json";
      return res.WriteAsync("{\"schemaVersion\":2}");
    }));

    myTokenServer = BuildKestrel(router => router.MapGet("token", (req, res, _) =>
    {
      Interlocked.Increment(ref myTokenRequests);
      myTokenAuthHeader = req.Headers.Authorization.ToString();
      myTokenScope = req.Query["scope"].ToString();

      res.ContentType = "application/json";
      return res.WriteAsync(JsonSerializer.Serialize(new { token = IssuedToken, expires_in = 300 }));
    }));
  }

  [Fact]
  public async Task A_Declared_Realm_Receives_The_Account_And_Its_Token_Goes_Upstream()
  {
    // No inbound credentials: the entry is PublicUpstream, so the prefix must stay anonymous.
    using var client = myProxyHost!.GetTestServer().CreateClient();

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
    using var client = myProxyHost!.GetTestServer().CreateClient();

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

  private string[] AuthSeenAt(string path) => [.. myRegistryAuthByPath[path]];

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
          PublicUpstream = true,
          TokenRealms = [UrlOf(myTokenServer).AbsoluteUri],
        },
        ["foreign"] = new UpstreamAuth
        {
          UrlPrefixes = [new Uri(registryUrl, "v2/other/").GetHostPortPath()],
          Username = Account,
          Password = Pat,
          PublicUpstream = true,
        },
      },
    };

    myProxyHost = new HostBuilder()
      .ConfigureWebHost(webHostBuilder => webHostBuilder
        .UseTestServer()
        .ConfigureAppConfiguration(cfg =>
          cfg.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(config))))
        .ConfigureOurServices()
        .ConfigureServices(services => services.AddSingleton(config))
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
