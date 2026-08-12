using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class RemoteServersTest
{
  private static RemoteServers.RemoteServer[] Build(params string[] prefixes) =>
  [
    .. new RemoteServers(new CachingProxyConfig { Prefixes = [.. prefixes.Select(p => (CachingProxyPrefix)p)] }, new NullLogger<RemoteServers>())
      .Endpoints
      .Select(e => e.Metadata.GetMetadata<RemoteServers.RemoteServer>()!)
  ];

  [Fact]
  public void Plain_Prefix_Targets_Itself_Over_Https()
  {
    var server = Assert.Single(Build("/a"));
    Assert.Equal("/a", server.Prefix);
    Assert.Equal("https://a/", server.RemoteUri.ToString());
  }

  [Fact]
  public void Alias_Maps_Prefix_To_Different_Target()
  {
    var server = Assert.Single(Build("/b=a"));
    Assert.Equal("/b", server.Prefix);
    Assert.Equal("https://a/", server.RemoteUri.ToString());
  }

  [Fact]
  public void Aliases_Resolve_To_The_Same_Upstream_Key()
  {
    // /a, /b=a and /c/d=a/ all point at https://a/, so the same artifact must yield the same key
    // regardless of which prefix was used to reach it.
    var servers = Build("/a", "/b=a", "/c/d=a/");
    Assert.Equal(new[] { "/a", "/b", "/c/d" }, servers.Select(s => s.Prefix.Value!).ToArray());
    var keys = servers.Select(s => s.GetUpstreamUri("a.jar").ManglePath()).Distinct();
    Assert.Equal("d9/6d/d96d0bd13935d4ab082c410dea64c70bf2f926b75f3b487ac18c0e290ee8ac3a", Assert.Single(keys));
  }

  [Fact]
  public void Absolute_Target_Is_Preserved()
  {
    var server = Assert.Single(Build("/p=http://example.org/sub"));
    Assert.Equal("http://example.org/sub/", server.RemoteUri.ToString());
  }

  [Fact]
  public void Upstream_Auth_Matches_By_Longest_Url_Prefix()
  {
    var hostWide = new UpstreamAuth { UrlPrefixes = ["repo.example.com/"], TokenEndpoint = new Uri("https://repo.example.com/"), ClientId = "host" };
    var pathScoped = new UpstreamAuth { UrlPrefixes = ["repo.example.com/secure/"], TokenEndpoint = new Uri("https://repo.example.com/"), ClientId = "scoped" };

    var config = new CachingProxyConfig
    {
      Prefixes =
      [
        "/a=repo.example.com/maven",        // → host-wide entry
        "/b=repo.example.com/secure/maven", // → longer, more specific entry wins
        "/c=other.example.com",             // → no match
      ],
      UpstreamAuth =
      {
        [nameof(hostWide)] = hostWide,
        [nameof(pathScoped)] = pathScoped,
      }
    };

    var servers = new RemoteServers(config, new NullLogger<RemoteServers>()).Endpoints
      .Select(e => e.Metadata.GetMetadata<RemoteServers.RemoteServer>()!)
      .ToArray();

    Assert.Same(hostWide, servers.Single(s => s.Prefix == "/a").Auth);
    Assert.Same(pathScoped, servers.Single(s => s.Prefix == "/b").Auth);
    Assert.Null(servers.Single(s => s.Prefix == "/c").Auth);
  }

  [Fact]
  public void An_Authenticated_Upstreams_Prefix_Requires_An_Inbound_Client_Jwt()
  {
    var privateRepo = new UpstreamAuth
    {
      UrlPrefixes = ["private.example.com/"], TokenEndpoint = new Uri("https://private.example.com/"), ClientId = "c",
    };
    // A registry account bought for rate limit is gated the same way: what it grants upstream is not
    // visible from here, so a credential counts as an access grant.
    var registryAccount = new UpstreamAuth
    {
      UrlPrefixes = ["registry-1.docker.io/"], Username = "svc", Password = "pat",
    };

    var config = new CachingProxyConfig
    {
      Prefixes = ["/a=private.example.com/maven", "/v2/docker-hub=registry-1.docker.io/v2", "/c=open.example.com"],
      UpstreamAuth = { [nameof(privateRepo)] = privateRepo, [nameof(registryAccount)] = registryAccount },
    };

    var gated = new RemoteServers(config, new NullLogger<RemoteServers>()).Endpoints
      .ToDictionary(
        e => e.Metadata.GetMetadata<RemoteServers.RemoteServer>()!.Prefix.Value!,
        e => e.Metadata.GetMetadata<IAuthorizeData>() != null);

    Assert.True(gated["/a"]);
    Assert.True(gated["/v2/docker-hub"]);
    Assert.False(gated["/c"]); // fetched anonymously, nothing to protect
  }

  [Fact]
  public void A_Matched_Entry_With_No_Credential_Is_Warned_About()
  {
    // The shape a half-configured secret takes: the entry gates its prefix inbound, so it looks
    // configured, while nothing is sent upstream. An OCI upstream then mints its registry token
    // anonymously, which a private repository answers with a 403 that names no missing account.
    var halfConfigured = new UpstreamAuth { UrlPrefixes = ["registry.example.com/v2/private"] };
    var account = new UpstreamAuth
    {
      UrlPrefixes = ["registry.example.com/v2/mirror"], Username = "svc", Password = "pat",
    };

    var config = new CachingProxyConfig
    {
      Prefixes = ["/v2/private=registry.example.com/v2/private", "/v2/mirror=registry.example.com/v2/mirror"],
      UpstreamAuth = { [nameof(halfConfigured)] = halfConfigured, [nameof(account)] = account },
    };

    var logger = new WarningLogger();
    _ = new RemoteServers(config, logger);

    var warning = Assert.Single(logger.Warnings);
    Assert.Equal(Event.IncompleteUpstreamAuth, warning.Event);
    Assert.Contains(nameof(halfConfigured), warning.Message);
    Assert.Contains("/v2/private", warning.Message);
  }

  // Collects what RemoteServers logged at Warning or above, so a startup misconfiguration can be asserted
  // on. The per-prefix Information lines are dropped, since IsEnabled gates them out.
  private sealed class WarningLogger : ILogger<RemoteServers>
  {
    public List<(EventId Event, string Message)> Warnings { get; } = [];

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter)
    {
      if (IsEnabled(logLevel)) Warnings.Add((eventId, formatter(state, exception)));
    }

    private sealed class Scope : IDisposable
    {
      public void Dispose() {}
    }
  }
}
