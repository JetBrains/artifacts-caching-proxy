using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
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
    var keys = servers.Select(s => s.GetUpstreamUri("a.jar")!.ManglePath()).Distinct();
    Assert.Equal("d9/6d/d96d0bd13935d4ab082c410dea64c70bf2f926b75f3b487ac18c0e290ee8ac3a", Assert.Single(keys));
  }

  [Fact]
  public void Absolute_Target_Is_Preserved()
  {
    var server = Assert.Single(Build("/p=http://example.org/sub"));
    Assert.Equal("http://example.org/sub/", server.RemoteUri.ToString());
  }

  // The shape MRI-4842 was reported against, reduced to its essentials: one host, a public subtree and a
  // private one. A remainder that climbs out of the public base lands on the private one's upstream path,
  // and therefore on its cache key - so the containment tests below are all asked of the public prefix.
  private static readonly RemoteServers.RemoteServer ourPublicPrefix =
    Build("/public=packages.example.com/maven/open", "/private=packages.example.com/maven/secure")
      .Single(s => s.Prefix == "/public");

  [Theory]
  // An absolute-path reference replaces the configured path but keeps the host - the MRI-4842 shape.
  [InlineData("/maven/secure/a.jar")]
  [InlineData("/maven/open2/a.jar")] // a sibling of the base, not a child of it
  [InlineData("/")]
  // A network-path reference replaces the authority - the MRI-4844 shape.
  [InlineData("//evil.example.com/maven/open/a.jar")]
  [InlineData("//packages.example.com@evil.example.com/x")] // the real host is the one after the '@'
  [InlineData("//user:pass@packages.example.com/maven/open/x")] // userinfo travels on, but keys nothing
  [InlineData("//packages.example.com:8443/maven/open/a.jar")] // another port is another service
  [InlineData("///a.jar")] // empty authority: does not resolve at all
  [InlineData("////a.jar")]
  // An absolute reference replaces everything, scheme included.
  [InlineData("https://evil.example.com/maven/open/a.jar")]
  [InlineData("http://packages.example.com/maven/open/a.jar")] // cleartext downgrade of the same target
  [InlineData("file://packages.example.com/maven/open/a.jar")] // same host+path form, not an HTTP fetch
  [InlineData("sha256:abcdef")] // a colon in the first segment reads as a scheme
  // Dot segments climb out - including percent-encoded ones, which System.Uri unescapes and collapses after
  // the request path was already checked for "..". A client reaches that shape by double-encoding
  // ("%252e%252e"): Kestrel decodes it once, leaving a remainder System.Uri then decodes again.
  [InlineData("a/../../secure/a.jar")]
  [InlineData("%2e%2e/secure/a.jar")]
  [InlineData(".%2e/secure/a.jar")]
  [InlineData("\r\n//evil.example.com/x")] // System.Uri strips CR/LF, exposing the reference beneath
  public void A_Remainder_That_Leaves_The_Configured_Base_Has_No_Upstream(string remainingPath) =>
    Assert.Null(ourPublicPrefix.GetUpstreamUri(remainingPath));

  [Theory]
  // A bare prefix request has no remainder at all: that is how an OCI client asks for "/v2/<alias>".
  [InlineData(null, "https://packages.example.com/maven/open/")]
  [InlineData("", "https://packages.example.com/maven/open/")]
  [InlineData("a.jar", "https://packages.example.com/maven/open/a.jar")]
  [InlineData("g/a/1.0/a-1.0.jar", "https://packages.example.com/maven/open/g/a/1.0/a-1.0.jar")]
  // ':' stays legal for OCI digest references, and %2f stays encoded for npm scoped packages.
  [InlineData("v2/img/manifests/sha256:abcdef", "https://packages.example.com/maven/open/v2/img/manifests/sha256:abcdef")]
  [InlineData("@scope%2fpackage", "https://packages.example.com/maven/open/@scope%2fpackage")]
  [InlineData("name with spaces.jar", "https://packages.example.com/maven/open/name%20with%20spaces.jar")]
  [InlineData("a//b.jar", "https://packages.example.com/maven/open/a//b.jar")] // an inner empty segment is the upstream's business
  [InlineData("sub/../a.jar", "https://packages.example.com/maven/open/a.jar")] // dot segments that stay inside
  // %2f is a character of the segment, not a separator, so this cannot climb out: it keys as the literal
  // escaped path. ValidateRequestAsync rejects the shape anyway, on the ".." in the request path.
  [InlineData("..%2f..%2fsecure%2fa.jar", "https://packages.example.com/maven/open/..%2f..%2fsecure%2fa.jar")]
  public void A_Remainder_Inside_The_Configured_Base_Resolves(string? remainingPath, string expected) =>
    Assert.Equal(expected, ourPublicPrefix.GetUpstreamUri(remainingPath)?.AbsoluteUri, ignoreCase: true);

  [Fact]
  public void A_Public_Prefix_Cannot_Resolve_To_A_Gated_Prefixs_Upstream()
  {
    // MRI-4842 end to end at this layer: /private carries a credential (and therefore [Authorize]) while
    // /public is anonymous, yet the cache key follows the resolved upstream. Spell out the resolution the
    // attack relied on, so this test cannot rot into asserting nothing.
    var auth = new UpstreamAuth
    {
      UrlPrefixes = ["packages.example.com/maven/secure"],
      TokenEndpoint = new Uri("https://packages.example.com/"),
      ClientId = "c",
    };
    var config = new CachingProxyConfig
    {
      Prefixes = ["/public=packages.example.com/maven/open", "/private=packages.example.com/maven/secure"],
      UpstreamAuth = { ["test"] = auth },
    };
    var servers = new RemoteServers(config, new NullLogger<RemoteServers>()).Endpoints
      .Select(e => e.Metadata.GetMetadata<RemoteServers.RemoteServer>()!)
      .ToDictionary(s => s.Prefix.Value!);

    Assert.Same(auth, servers["/private"].Auth);
    Assert.Null(servers["/public"].Auth);

    var privateArtifact = servers["/private"].GetUpstreamUri("a.jar")!;
    Assert.Equal(privateArtifact.AbsoluteUri, new Uri(servers["/public"].RemoteUri, "/maven/secure/a.jar").AbsoluteUri);
    Assert.Null(servers["/public"].GetUpstreamUri("/maven/secure/a.jar"));
  }

  [Fact]
  public void A_Host_Root_Base_Contains_Every_Path_On_Its_Own_Host()
  {
    // Containment is scoped to the configured base, so a prefix configured for a whole host really does
    // reach every path on it - which is why a leading slash is harmless there (see
    // CacheFileProviderTest.ManglePath_LeadingSlashIsIgnored). Another host still is not reachable.
    var server = Assert.Single(Build("/h=packages.example.com"));
    Assert.Equal("https://packages.example.com/a.jar", server.GetUpstreamUri("/a.jar")?.AbsoluteUri);
    Assert.Null(server.GetUpstreamUri("//evil.example.com/a.jar"));
  }

  [Fact]
  public void A_Configured_Target_Always_Ends_In_A_Slash() =>
    // The invariant the sibling rejection above rests on: without the trailing slash, "/open2/a.jar" would
    // read as a child of "/open".
    Assert.All(Build("/a=h.example.com/open", "/b=h.example.com/open/", "/c=h.example.com"),
      server => Assert.EndsWith("/", server.RemoteUri.AbsolutePath, StringComparison.Ordinal));

  [Fact]
  public void An_Upstream_Auth_Entry_Without_UrlPrefixes_Is_Rejected_At_Startup()
  {
    // Such an entry matches no upstream, so the prefixes it was meant to gate would serve their cached
    // contents to anyone. The message has to name the entry: that is the only pointer to the setting.
    var config = new CachingProxyConfig
    {
      Prefixes = ["/a=packages.example.com/maven/secure"],
      UpstreamAuth = { ["half_configured"] = new UpstreamAuth { UrlPrefixes = [], ClientId = "c" } },
    };

    var error = Assert.Throws<ArgumentException>(() => new RemoteServers(config, new NullLogger<RemoteServers>()));

    Assert.Contains("half_configured", error.Message);
    Assert.Contains(nameof(UpstreamAuth.UrlPrefixes), error.Message);
  }

  [Fact]
  public void A_Bound_Upstream_Auth_Entry_Never_Has_Null_UrlPrefixes()
  {
    // `required` is a compile-time rule for object initializers, and configuration binding constructs by
    // reflection - so a deployment that set the credential half of a block but not UrlPrefixes bound to null
    // and crashed startup from inside MatchAuth's LINQ. It must bind empty and be reported by name instead.
    const string json = """
      {
        "Prefixes": [ "/a=packages.example.com/maven/secure" ],
        "UpstreamAuth": { "half_configured": { "ClientId": "c", "ClientSecret": "s" } }
      }
      """;
    var config = new ConfigurationBuilder()
      .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
      .Build()
      .Get<CachingProxyConfig>()!;

    Assert.Empty(config.UpstreamAuth["half_configured"].UrlPrefixes);
    var error = Assert.Throws<ArgumentException>(() => new RemoteServers(config, new NullLogger<RemoteServers>()));
    Assert.Contains("half_configured", error.Message);
  }

  [Theory]
  [InlineData("/a=h.example.com/p?x=1")]
  [InlineData("/a=h.example.com/p#f")]
  public void A_Target_With_A_Query_Or_Fragment_Is_Rejected_At_Startup(string prefix) =>
    // Such a target has no trailing-slash path to resolve against, so every request to it would resolve
    // outside the base and be rejected. Better to never start than to 400 in production.
    Assert.Throws<ArgumentException>(() => Build(prefix));

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
