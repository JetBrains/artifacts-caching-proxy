using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ZiggyCreatures.Caching.Fusion;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class TokenClientRetryTest
{
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

  [Fact]
  public async Task RegistryTokenProvider_Retries_On_Transient_Error()
  {
    var attempts = 0;
    var server = BuildKestrel(router =>
    {
      router.MapGet("token", (_, res, _) =>
      {
        var attempt = Interlocked.Increment(ref attempts);
        if (attempt == 1)
        {
          res.StatusCode = StatusCodes.Status503ServiceUnavailable;
          return Task.CompletedTask;
        }

        res.ContentType = "application/json";
        return res.WriteAsync(JsonSerializer.Serialize(new { token = "resilient-registry-token" }));
      });
    });

    await server.StartAsync();
    try
    {
      var serverUrl = UrlOf(server);

      var services = new ServiceCollection();
      var config = new ConfigurationBuilder().Build();
      services.AddSingleton(TimeProvider.System);
      services.AddUpstreamAuth(config);
      services.AddFusionCache();
      using var sp = services.BuildServiceProvider();

      var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
      var cache = sp.GetRequiredService<IFusionCache>();
      var provider = new RegistryTokenProvider(httpClientFactory, cache, NullLogger<RegistryTokenProvider>.Instance);

      var challenge = new RegistryChallenge(new Uri(serverUrl, "token"), "service", "repository:test:pull");
      var upstreamUri = new Uri(serverUrl, "v2/test/manifests/latest");
      var token = await provider.GetTokenAsync(challenge, upstreamUri, null, null, CancellationToken.None);

      Assert.Equal("resilient-registry-token", token);
      Assert.Equal(2, attempts);
    }
    finally
    {
      await server.StopAsync();
    }
  }

  [Fact]
  public async Task GitHubAppInstallationTokenProvider_Retries_On_Transient_Error()
  {
    var attempts = 0;
    using var rsa = RSA.Create(2048);
    var pemKey = rsa.ExportRSAPrivateKeyPem();

    var server = BuildKestrel(router =>
    {
      router.MapGet("app/installations", (req, res, _) =>
      {
        var attempt = Interlocked.Increment(ref attempts);
        if (attempt == 1)
        {
          res.StatusCode = StatusCodes.Status502BadGateway;
          return Task.CompletedTask;
        }

        res.ContentType = "application/json";
        var accessTokensUrl = $"{req.Scheme}://{req.Host}/app/installations/1/access_tokens";
        return res.WriteAsync(JsonSerializer.Serialize(new[] { new { id = 1, access_tokens_url = accessTokensUrl } }));
      });

      router.MapPost("app/installations/{id}/access_tokens", (_, res, _) =>
      {
        res.ContentType = "application/json";
        return res.WriteAsync(JsonSerializer.Serialize(new
        {
          token = "resilient-github-token",
          expires_at = DateTime.UtcNow.AddHours(1),
        }));
      });
    });

    await server.StartAsync();
    try
    {
      var serverUrl = UrlOf(server);

      var services = new ServiceCollection();
      var config = new ConfigurationBuilder().Build();
      services.AddSingleton(TimeProvider.System);
      services.AddUpstreamAuth(config);
      services.AddFusionCache();
      using var sp = services.BuildServiceProvider();

      var provider = sp.GetRequiredService<GitHubAppInstallationTokenProvider>();
      var auth = new UpstreamAuth
      {
        UrlPrefixes = ["github.com/"],
        ClientId = "test-app",
        PrivateKey = pemKey,
        GitHubApiBaseUrl = serverUrl.ToString(),
      };

      var token = await provider.GetInstallationTokenAsync(auth, CancellationToken.None);

      Assert.Equal("resilient-github-token", token);
      Assert.Equal(2, attempts);
    }
    finally
    {
      await server.StopAsync();
    }
  }

  [Fact]
  public async Task ClientCredentials_UpstreamAuth_Retries_On_Transient_Error()
  {
    var attempts = 0;
    var server = BuildKestrel(router =>
    {
      router.MapPost("token", (_, res, _) =>
      {
        var attempt = Interlocked.Increment(ref attempts);
        if (attempt == 1)
        {
          res.StatusCode = StatusCodes.Status500InternalServerError;
          return Task.CompletedTask;
        }

        res.ContentType = "application/json";
        return res.WriteAsync(JsonSerializer.Serialize(new
        {
          access_token = "resilient-oauth-token",
          token_type = "Bearer",
          expires_in = 3600,
        }));
      });
    });

    await server.StartAsync();
    try
    {
      var serverUrl = UrlOf(server);

      var inMemoryConfig = new Dictionary<string, string?>
      {
        ["UpstreamAuth:client:ClientId"] = "test-client",
        ["UpstreamAuth:client:ClientSecret"] = "test-secret",
        ["UpstreamAuth:client:TokenEndpoint"] = new Uri(serverUrl, "token").ToString(),
        ["UpstreamAuth:client:UrlPrefixes:0"] = "upstream.example.com",
      };

      var config = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton(TimeProvider.System);
      services.AddDistributedMemoryCache();
      services.AddFusionCache();
      services.AddUpstreamAuth(config);
      using var sp = services.BuildServiceProvider();

      var provider = sp.GetRequiredService<IUpstreamAuthorizationProvider>();
      var auth = new UpstreamAuth
      {
        UrlPrefixes = ["upstream.example.com/"],
        ClientId = "test-client",
        ClientSecret = "test-secret",
        TokenEndpoint = new Uri(serverUrl, "token"),
      };

      var header = await provider.GetAuthorizationHeaderAsync(auth, CancellationToken.None);

      Assert.NotNull(header);
      Assert.Equal("Basic", header.Scheme);
      var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!));
      Assert.Equal("test-client:resilient-oauth-token", decoded);
      Assert.Equal(2, attempts);
    }
    finally
    {
      await server.StopAsync();
    }
  }
}
