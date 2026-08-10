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
        res.StatusCode = (int)HttpStatusCode.Unauthorized;
        res.Headers["WWW-Authenticate"] =
          $"Bearer realm=\"{UrlOf(myTokenServer!)}token\",service=\"registry.test\"";
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

    // The account is presented at the token endpoint, as Basic, and the scope is the repository being
    // pulled - not the alias the client used.
    Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Account}:{Pat}")), myTokenAuthHeader);
    Assert.Equal("repository:library/allowed:pull", myTokenScope);

    // One token for both tags: same scope, same account, so it is minted once and reused.
    Assert.Equal(1, myTokenRequests);

    // The first pull pays the 401 and retries with the minted token. Note the PAT is offered upstream too
    // (a registry that takes Basic directly then needs no dance at all), and is replaced on the retry.
    Assert.Equal(
      ["Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Account}:{Pat}")), $"Bearer {IssuedToken}"],
      AuthSeenAt("v2/library/allowed/manifests/1.0"));

    // The second pull pays no 401: the realm learned from the first is reused to mint up front.
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
    Assert.Equal("repository:library/foreign:pull", myTokenScope);
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
        // The same registry mounted under another path, so the two prefixes get different UpstreamAuth
        // entries (matched by longest URL prefix) while sharing one challenge.
        new CachingProxyPrefix($"/v2/foreign={registryUrl}other/v2", Profile: "docker"),
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
          UrlPrefixes = [new Uri(registryUrl, "other/v2/").GetHostPortPath()],
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
