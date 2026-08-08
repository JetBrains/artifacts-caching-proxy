using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace JetBrains.CachingProxy.Tests;

// TODO: Negative caching expiration per status code
// TODO: Switch to real server in tests
[SuppressMessage("ReSharper", "UnusedParameter.Local")]
public class CachingProxyTest : IAsyncLifetime, IClassFixture<UpstreamTestServer>
{
  private readonly ITestOutputHelper myOutput;
  private readonly IHost myHost;
  private readonly TestServer myServer;
  private readonly string myTempDirectory;
  private readonly UpstreamTestServer myUpstreamServer;
  private readonly CachingProxyConfig myConfig;
  private readonly FakeTimeProvider myTimeProvider;
  // Extra hosts spun up by individual tests (e.g. with a custom freshness profile), stopped on dispose.
  private readonly List<IHost> myExtraHosts = [];

  public CachingProxyTest(ITestOutputHelper output, UpstreamTestServer upstreamServer)
  {
    myOutput = output;
    myTempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(myTempDirectory);

    myConfig = new CachingProxyConfig
    {
      LocalCachePath = myTempDirectory,
      CachingProfiles = new Dictionary<string, CachingProfile>
      {
        // The Maven profile: mutable metadata is revalidated hourly, SNAPSHOTs daily, and everything
        // else (immutable coordinates) matches no rule => cached forever.
        ["maven"] = new()
        {
          Rules =
          [
            new CachingRule { Pattern = @"maven-metadata\.xml(\..+)?$", RefreshAfter = TimeSpan.FromHours(1) },
            new CachingRule { Pattern = @"archetype-catalog\.xml(\..+)?$", RefreshAfter = TimeSpan.FromHours(1) },
            new CachingRule { Pattern = "-SNAPSHOT", RefreshAfter = TimeSpan.FromDays(1) },
          ]
        },
        // Exercises the Redirect rule action: any path is always 307-redirected to the upstream.
        ["redirect-all"] = new() { Rules = [new CachingRule { Pattern = ".", Redirect = true }] },
        // The npm profile (full-only packument caching): security/search redirect (dynamic), tarballs
        // are cached forever (immutable), and everything else — packuments, version docs, dist-tags —
        // is cached with a short freshness window and revalidated.
        ["npm"] = new()
        {
          Rules =
          [
            new CachingRule { Pattern = "-/npm/v1/security/", Redirect = true },
            new CachingRule { Pattern = "-/v1/search", Redirect = true },
            new CachingRule { Pattern = @"\.tgz$" },
            new CachingRule { Pattern = ".", RefreshAfter = TimeSpan.FromHours(1) },
          ]
        },
        // The docker profile, in the shipped order (appsettings.json): digest-addressed blobs and
        // manifests are content-addressed and cached forever, tag-addressed content revalidates on a
        // short window, _catalog is a dynamic listing. Oci makes the prefix speak the registry token
        // dance upstream.
        ["docker"] = new()
        {
          Oci = true,
          Rules =
          [
            new CachingRule { Pattern = @"/blobs/[a-z0-9]+(?:[+._-][a-z0-9]+)*:[0-9a-fA-F]{32,}$" },
            new CachingRule { Pattern = @"/manifests/[a-z0-9]+(?:[+._-][a-z0-9]+)*:[0-9a-fA-F]{32,}$", VaryByAccept = true },
            new CachingRule { Pattern = "/manifests/", RefreshAfter = TimeSpan.FromMinutes(5), VaryByAccept = true },
            new CachingRule { Pattern = "/tags/list", RefreshAfter = TimeSpan.FromMinutes(5) },
            new CachingRule { Pattern = "/referrers/", RefreshAfter = TimeSpan.FromMinutes(5) },
            new CachingRule { Pattern = "/_catalog", Redirect = true },
            new CachingRule { Pattern = ".", RefreshAfter = TimeSpan.FromMinutes(5) },
          ]
        },
      },
      Prefixes =
      [
        "/repo1.maven.org/maven2",
        "/198.51.100.9",
        "/plugins.gradle.org/m2",
        "/registry.npmjs.org",
        "/unknown_host.xyz",
        // Overlapping prefixes, deliberately listed shortest-first: the more specific prefix must win
        // regardless of declaration order. The shorter one points at a non-existent upstream subpath so
        // that a wrong (shorter) match yields a 404 instead of the expected body.
        $"/overlap={upstreamServer.Url}wrong/",
        $"/overlap/nested={upstreamServer.Url}",
        new CachingProxyPrefix($"/real={upstreamServer.Url}", Profile: "maven"),
        new CachingProxyPrefix($"/real-redirect={upstreamServer.Url}", Profile: "redirect-all"),
        new CachingProxyPrefix($"/real-npm={upstreamServer.Url}", Profile: "npm"),
        // An OCI prefix carries the "/v2" on both sides, exactly as config-gen emits it: the client
        // inserts "/v2/" after the host itself, so the alias is "/v2/<name>" and the origin ends in /v2.
        new CachingProxyPrefix($"/v2/real-docker={upstreamServer.Url}v2", Profile: "docker"),
        new CachingProxyPrefix($"/real-custom-ttl={upstreamServer.Url}", new CacheDuration
        {
          [HttpStatusCode.OK] = TimeSpan.FromMinutes(30),
          [HttpStatusCode.NotFound] = TimeSpan.FromMinutes(15),
        })
      ],
      MinimumFreeDiskSpaceMb = 2,
      UserAgentComment = "(+mailto:cache-redirector@jetbrains.com)",
      CleanupInterval = "* 0 * * *",
      CleanupPeriod =  TimeSpan.FromDays(1)
    };

    // Start the fake clock at the real "now" so it shares an era with FusionCache, which derives
    // its L1 entry AbsoluteExpiration from the real system clock (it has no TimeProvider hook).
    // With the default year-2000 epoch, advancing the fake clock would never reach the ~year-2026
    // expiration and cache entries would never evict. See ResponseCacheTest for the full rationale.
    myTimeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
    myHost = new HostBuilder()
      .ConfigureWebHost(webHostBuilder =>
      {
        webHostBuilder
          .UseTestServer()
          .ConfigureAppConfiguration(cfg =>
            cfg.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(myConfig))))
          .ConfigureOurServices()
          .ConfigureServices(services => services
            .AddSingleton(myConfig)
            .Replace(ServiceDescriptor.Singleton<TimeProvider>(myTimeProvider)))
          .Configure((context, builder) => builder.ConfigureOurApp(context.Configuration));
      })
      .Build();
    myServer = myHost.GetTestServer();

    myUpstreamServer = upstreamServer;
  }

  [Fact]
  public async Task Health_OK()
  {
    await AssertGetResponse("/health", HttpStatusCode.OK,
      (message, bytes) =>
      {
        Assert.Equal("OK", Encoding.UTF8.GetString(bytes));
      });
  }

  [Fact]
  public async Task Caching_Works()
  {
    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("application/java-archive", message.Content.Headers.ContentType?.ToString());
        Assert.Equal(11541, GetContentLength(message));
        Assert.Equal(11541, bytes.Length);
        Assert.Equal("eca06bb19a4f55673f8f40d0a20eb0ee0342403ee5856b890d6c612e5facb027", SHA256(bytes));
        Assert.Equal("Tue, 10 Jul 2018 04:58:42 GMT", message.Content.Headers.GetValues("Last-Modified").Single());
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
      });

    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal(11541, GetContentLength(message));
        Assert.Equal("application/java-archive", message.Content.Headers.ContentType?.ToString());
        Assert.Equal(11541, bytes.Length);
        Assert.Equal("eca06bb19a4f55673f8f40d0a20eb0ee0342403ee5856b890d6c612e5facb027", SHA256(bytes));
        Assert.Equal("Tue, 10 Jul 2018 04:58:42 GMT", message.Content.Headers.GetValues("Last-Modified").Single());
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
      });

    Assert.Equal(11541, new FileInfo(
      Path.Combine(myTempDirectory, "2b/0b/2b0b5f703eb2ed34d6f0c4fb31fa6f3dc4d224f91ec8aaa51bc36f518ca54168.jar")).Length);
  }

  [Fact]
  public async Task Get_Followed_By_Head()
  {
    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
      (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));

    await AssertHeadResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
      message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal(11541, GetContentLength(message));
        Assert.Equal("application/java-archive", message.Content.Headers.ContentType?.ToString());
        Assert.Equal("Tue, 10 Jul 2018 04:58:42 GMT", message.Content.Headers.GetValues("Last-Modified").Single());
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
      });
  }

  [Fact]
  public async Task Files_In_Hierarchy()
  {
    await AssertGetResponse("/real/a.jar", HttpStatusCode.OK, (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));
    await AssertGetResponse("/real/a.jar/b.jar", HttpStatusCode.OK, (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));

    await AssertGetResponse("/real/a.jar", HttpStatusCode.OK, (message, bytes) =>           AssertStatusHeader(message, CachingProxyStatus.HIT));
    await AssertGetResponse("/real/a.jar/b.jar", HttpStatusCode.OK, (message, bytes) =>           AssertStatusHeader(message, CachingProxyStatus.HIT));
  }

  [Fact]
  public async Task Remote_CacheHtmlFile()
  {
    await AssertGetResponse("/real/a.html", HttpStatusCode.OK, (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));
  }

  [Fact]
  public async Task File_Name_With_Spaces()
  {
    await AssertGetResponse("/real/name with spaces.jar", HttpStatusCode.OK, (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));
  }

  [Fact]
  public async Task Content_Encoding_Is_Preserved()
  {
    await AssertGetResponse("/real/gzipEncoding.txt", HttpStatusCode.OK, (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        // 37 - gzipped response length
        Assert.Equal(37, bytes.Length);
        Assert.Equal("gzip", message.Content.Headers.ContentEncoding.SingleOrDefault());
      }
    );

    await AssertGetResponse("/real/gzipEncoding.txt", HttpStatusCode.OK, (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal(37, GetContentLength(message));
        Assert.Equal("gzip", message.Content.Headers.ContentEncoding.SingleOrDefault());
      }
    );
  }

  [Fact]
  public async Task Content_Encoding_Is_Preserved_Head_Request()
  {
    await AssertHeadResponse("/real/gzipEncoding.txt", HttpStatusCode.OK, message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        // 37 - gzipped response length
        Assert.Equal(37, GetContentLength(message));
        Assert.Equal("gzip", message.Content.Headers.ContentEncoding.SingleOrDefault());
      }
    );

    await AssertHeadResponse("/real/gzipEncoding.txt", HttpStatusCode.OK, message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        // 37 - gzipped response length
        Assert.Equal(37, GetContentLength(message));
        Assert.Equal("gzip", message.Content.Headers.ContentEncoding.SingleOrDefault());
      }
    );
  }

  [Fact]
  public async Task Content_Encoding_Is_Cached_For_Head_Response()
  {
    await AssertGetResponse("/real/gzipEncoding.txt", HttpStatusCode.OK, (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        // 37 - gzipped response length
        Assert.Equal(37, bytes.Length);
        Assert.Equal("gzip", message.Content.Headers.ContentEncoding.SingleOrDefault());
      }
    );

    await AssertHeadResponse("/real/gzipEncoding.txt", HttpStatusCode.OK, message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        // 37 - gzipped response length
        Assert.Equal(37, GetContentLength(message));
        Assert.Equal("gzip", message.Content.Headers.ContentEncoding.SingleOrDefault());
      }
    );
  }

  [Fact]
  public async Task Only_Gzip_Encoding_Supported_In_Content_Encoding()
  {
    await AssertGetResponse("/real/fakeBrEncoding.txt", HttpStatusCode.ServiceUnavailable, (message, bytes) =>
    {
      Assert.Equal($"{myUpstreamServer.Url}fakeBrEncoding.txt returned Content-Encoding 'br' which is not supported", Encoding.UTF8.GetString(bytes));
    });
  }

  [Fact]
  public async Task Multiple_Encodings_Are_Not_Supported_In_Content_Encoding()
  {
    await AssertGetResponse("/real/fakeMultipleEncodings.txt", HttpStatusCode.ServiceUnavailable, (message, bytes) =>
    {
      Assert.Equal($"{myUpstreamServer.Url}fakeMultipleEncodings.txt returned multiple Content-Encoding which is not allowed: deflate, gzip", Encoding.UTF8.GetString(bytes));
    });
  }

  [Fact]
  public async Task File_Name_With_Plus()
  {
    await AssertGetResponse("/real/name+with+plus.jar", HttpStatusCode.OK, (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));
  }

  [Fact]
  public async Task Path_With_At_Symbol()
  {
    await AssertGetResponse("/real/@username/package/-/package-3.1.2.tgz", HttpStatusCode.OK, (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));
  }

  [Fact]
  public async Task Path_With_Percent_Encoded_Slash()
  {
    // npm scoped packages use %2f in registry URLs (e.g. @types%2fserve-index).
    // The %2f must be accepted (not rejected as BAD_REQUEST) and proxied to the upstream.
    await AssertGetResponse("/real/@scope%2fpackage", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("scoped-package-content", Encoding.UTF8.GetString(bytes));
      });

    // Direct guard: the proxy must forward the slash still percent-encoded, not as a real '/'.
    // (Hex case may be normalized by System.Uri, so compare case-insensitively.)
    Assert.Contains("@scope%2f", myUpstreamServer.LastRawTarget, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("@scope/package", myUpstreamServer.LastRawTarget);
  }

  [Fact]
  public async Task Path_With_Multiple_Slashes_Is_Bad_Request()
  {
    // A degenerate URL such as "/maven-central////-.jar": the catch-all captures "///-.jar",
    // whose leading "//" makes new Uri(base, ...) resolve to an empty authority (invalid for
    // http(s)). The proxy must reject it as 400 BAD_REQUEST, not crash with a 500.
    await AssertGetResponse("/real////-.jar", HttpStatusCode.BadRequest,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.BAD_REQUEST);
        Assert.Equal("Invalid request path", Encoding.UTF8.GetString(bytes));
      });
  }

  [Fact]
  public async Task Empty_File_Extension_Is_Cached()
  {
    // MRI-4508: extensionless paths are no longer redirected to the remote;
    // they are cached and served with the default content type.
    await AssertGetResponse("/real/extensionless", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal(MediaTypeNames.Application.Octet, message.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-extension-content", Encoding.UTF8.GetString(bytes));
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
      });

    await AssertGetResponse("/real/extensionless", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal(MediaTypeNames.Application.Octet, message.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-extension-content", Encoding.UTF8.GetString(bytes));
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
      });
  }

  [Fact]
  public async Task Empty_File_Extension_Head_Is_Cached()
  {
    // MRI-4508: HEAD on an extensionless path is served with the default content type
    // (instead of being redirected) and is cached in-memory for subsequent HEAD requests.
    await AssertHeadResponse("/real/extensionless", HttpStatusCode.OK,
      message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal(MediaTypeNames.Application.Octet, message.Content.Headers.ContentType?.ToString());
      });

    // Second HEAD is served from the in-memory positive cache and must carry
    // the same default content type as the MISS above.
    await AssertHeadResponse("/real/extensionless", HttpStatusCode.OK,
      message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal(MediaTypeNames.Application.Octet, message.Content.Headers.ContentType?.ToString());
      });
  }

  [Fact]
  public async Task Retry_After_500()
  {
    myUpstreamServer.Conditional500SendErrorOnce = true;
    await AssertGetResponse("/real/conditional-500.txt", HttpStatusCode.OK, (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));
  }

  [Fact]
  public async Task Post()
  {
    await AssertPostResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.MethodNotAllowed,
      message =>
      {

      });
  }

  [Fact]
  public async Task Head_With_Existing_File()
  {
    await AssertHeadResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
      message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);

        Assert.Equal("application/java-archive", message.Content.Headers.ContentType?.ToString());
        Assert.Equal(11541, GetContentLength(message));
        Assert.Equal("Tue, 10 Jul 2018 04:58:42 GMT", message.Content.Headers.GetValues("Last-Modified").Single());
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
      });
    await AssertHeadResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
      message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);

        Assert.Equal("application/java-archive", message.Content.Headers.ContentType?.ToString());
        Assert.Equal(11541, GetContentLength(message));
        Assert.Equal("Tue, 10 Jul 2018 04:58:42 GMT", message.Content.Headers.GetValues("Last-Modified").Single());
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
      });
  }

  [Fact]
  public async Task Head_With_Missing_File()
  {
    await AssertHeadResponse("/repo1.maven.org/maven2/notfound.txt", HttpStatusCode.NotFound,
      message => AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS));
    await AssertHeadResponse("/repo1.maven.org/maven2/notfound.txt", HttpStatusCode.NotFound,
      message => AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT));
  }

  [Fact]
  public async Task Caching_Works_Unknown_ContentLength()
  {
    const string url = "/real/a.jar";
    await AssertGetResponse(url, HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Null(GetContentLength(message));
        Assert.Equal("a.jar", Encoding.UTF8.GetString(bytes));
      });

    await AssertGetResponse(url, HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal(5, GetContentLength(message));
        Assert.Equal("a.jar", Encoding.UTF8.GetString(bytes));
      });
  }

  [Fact]
  public async Task Parallel_Requests()
  {
    const string url = "/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar";

    using var response1 = myServer.CreateRequest(url).GetAsync();
    using var response2 = myServer.CreateRequest(url).GetAsync();

    var result = await Task.WhenAll(response1, response2);

    AssertStatusHeader(result[0], CachingProxyStatus.MISS);
    AssertStatusHeader(result[1], CachingProxyStatus.MISS);

    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
      (message, bytes) => { AssertStatusHeader(message, CachingProxyStatus.HIT); });
  }

  [Fact]
  public async Task Maven_Snapshot_Is_Cached_With_Daily_Freshness()
  {
    // A SNAPSHOT coordinate is mutable: the maven profile caches it (no longer an ALWAYS_REDIRECT) and
    // advertises its 1-day freshness window as the client max-age.
    await AssertGetResponse("/real/group/artifact/1.0-SNAPSHOT/artifact-1.0-SNAPSHOT.jar", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("public, max-age=86400", message.Headers.CacheControl?.ToString());
        Assert.Equal("snapshot-jar-content", Encoding.UTF8.GetString(bytes));
      });

    await AssertGetResponse("/real/group/artifact/1.0-SNAPSHOT/artifact-1.0-SNAPSHOT.jar", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal("public, max-age=86400", message.Headers.CacheControl?.ToString());
      });
  }

  [Fact]
  public async Task Maven_Archetype_Catalog_Is_Cached_With_Hourly_Freshness()
  {
    // archetype-catalog.xml lives at the repository root and is mutable (updated as archetypes are
    // deployed): the maven profile caches it with the 1-hour freshness window rather than forever.
    await AssertGetResponse("/real/archetype-catalog.xml", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("public, max-age=3600", message.Headers.CacheControl?.ToString());
        Assert.Equal("<archetype-catalog/>", Encoding.UTF8.GetString(bytes));
      });

    await AssertGetResponse("/real/archetype-catalog.xml", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal("public, max-age=3600", message.Headers.CacheControl?.ToString());
      });
  }

  [Fact]
  public async Task Maven_Immutable_Keeps_Eternal_Caching()
  {
    // An immutable coordinate on the maven-profiled prefix matches no rule => cached forever, with the
    // eternal 365-day max-age (not the shorter freshness windows the profile gives mutable endpoints).
    await AssertGetResponse("/real/a.jar", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
      });
  }

  [Fact]
  public async Task Redirect_Rule_Always_Redirects()
  {
    // A profile Redirect rule bounces the path to the upstream with a 307 (never caching it) — the
    // mechanism the npm profile uses for its non-cacheable security-audit endpoint.
    await AssertGetResponse("/real-redirect/a.jar", HttpStatusCode.RedirectKeepVerb,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
        Assert.Equal($"{myUpstreamServer.Url}a.jar", message.Headers.Location?.ToString());
      });
  }

  [Fact]
  public async Task Npm_Packument_Is_Cached_And_Revalidated()
  {
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;

    // A packument is a bare package path (no file extension). The npm profile caches it (never
    // redirects) and advertises the 1-hour freshness window as the client max-age.
    await AssertGetResponse("/real-npm/express", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("public, max-age=3600", message.Headers.CacheControl?.ToString());
        Assert.Contains("\"name\":\"express\"", Encoding.UTF8.GetString(bytes));
      });

    await AssertGetResponse("/real-npm/express", HttpStatusCode.OK,
      (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.HIT));

    // Past the window it is revalidated; the upstream answers 304, so it is kept and served.
    myTimeProvider.Advance(TimeSpan.FromMinutes(61));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotModified;
    await AssertGetResponse("/real-npm/express", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.REVALIDATED);
        Assert.Contains("\"name\":\"express\"", Encoding.UTF8.GetString(bytes));
      });
  }

  [Fact]
  public async Task Npm_Packument_Served_Stale_When_Upstream_Down()
  {
    // The offline guarantee: once cached, a packument is still served (not redirected, not failed)
    // when the upstream is unreachable during revalidation.
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    await AssertGetResponse("/real-npm/express", HttpStatusCode.OK,
      (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));

    myTimeProvider.Advance(TimeSpan.FromMinutes(61));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.ServerError;
    await AssertGetResponse("/real-npm/express", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.STALE);
        Assert.Contains("\"name\":\"express\"", Encoding.UTF8.GetString(bytes));
      });
  }

  [Fact]
  public async Task Npm_Tarball_Is_Cached_Eternally()
  {
    // A version tarball is immutable: the .tgz rule (before the catch-all) caches it forever.
    await AssertGetResponse("/real-npm/express/-/express-1.0.0.tgz", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
        Assert.Equal("npm-tarball-content", Encoding.UTF8.GetString(bytes));
      });

    await AssertGetResponse("/real-npm/express/-/express-1.0.0.tgz", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
      });
  }

  [Fact]
  public async Task Npm_Security_Audit_Is_Redirected()
  {
    // The security-audit endpoint is dynamic (not meaningful offline data), so it is redirected.
    await AssertGetResponse("/real-npm/-/npm/v1/security/audits/quick", HttpStatusCode.RedirectKeepVerb,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
        Assert.Equal($"{myUpstreamServer.Url}-/npm/v1/security/audits/quick", message.Headers.Location?.ToString());
      });
  }

  [Fact]
  public async Task Oci_Base_Endpoint_Answers_The_Ping()
  {
    // Every registry client probes the base endpoint before it fetches anything and abandons the pull
    // unless that answers 2xx. It is not a proxied path, so it carries no proxy status header.
    await AssertGetResponse("/v2/", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertNoStatusHeader(message);
        Assert.Equal(CachingProxyConstants.DockerApiVersion,
          message.Headers.GetValues(CachingProxyConstants.DockerApiVersionHeader).Single());
        Assert.Equal(MediaTypeNames.Application.Json, message.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{}", Encoding.UTF8.GetString(bytes));
      });

    // The bare form redirects to it, as a real registry does, and clients follow that.
    await AssertGetResponse("/v2", HttpStatusCode.RedirectKeepVerb,
      (message, bytes) => Assert.Equal("/v2/", message.Headers.Location?.ToString()));
  }

  [Fact]
  public async Task Oci_Manifest_By_Digest_Is_Cached_Eternally()
  {
    // A digest reference is the shape that used to be rejected outright: ':' was not in the allowed path
    // characters, so every layer and every resolved manifest came back 400.
    var path = $"/v2/real-docker/testimage/manifests/{UpstreamTestServer.ManifestDigest}";

    // Content-addressed, so the eternal (no window) rule applies and the digest header - which a client
    // verifies the body against - has to survive the round trip through the cache.
    foreach (var expectedStatus in new[] { CachingProxyStatus.MISS, CachingProxyStatus.HIT })
    {
      await AssertGetResponse(path, HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertStatusHeader(message, expectedStatus);
          Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
          Assert.Equal(UpstreamTestServer.ManifestDigest,
            message.Headers.GetValues(CachedResponse.DockerContentDigestHeader).Single());
          Assert.Equal(UpstreamTestServer.ManifestMediaType, message.Content.Headers.ContentType?.ToString());
          Assert.Equal("{\"layers\":[]}", Encoding.UTF8.GetString(bytes));
        });
    }

    // The colon reached the upstream verbatim rather than percent-encoded: a registry matches the
    // reference literally.
    Assert.Equal($"/v2/testimage/manifests/{UpstreamTestServer.ManifestDigest}", myUpstreamServer.LastRawTarget);
  }

  [Fact]
  public async Task Oci_Blob_Is_Cached_Eternally()
  {
    var path = $"/v2/real-docker/testimage/blobs/{UpstreamTestServer.BlobDigest}";
    var before = myUpstreamServer.BlobRequestCount;

    foreach (var expectedStatus in new[] { CachingProxyStatus.MISS, CachingProxyStatus.HIT })
    {
      await AssertGetResponse(path, HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertStatusHeader(message, expectedStatus);
          Assert.Equal("public, max-age=31536000", message.Headers.CacheControl?.ToString());
          Assert.Equal(UpstreamTestServer.BlobDigest,
            message.Headers.GetValues(CachedResponse.DockerContentDigestHeader).Single());
          Assert.Equal("blob-content", Encoding.UTF8.GetString(bytes));
        });
    }

    // A layer is what the cache exists for: fetched once, served from the copy afterwards.
    Assert.Equal(before + 1, myUpstreamServer.BlobRequestCount);
  }

  [Fact]
  public async Task Oci_Manifest_Representations_Are_Cached_Separately()
  {
    // One tag, two representations. Without Accept in the cache key the first client's choice would be
    // served to every later one, so a client that asked for a single-arch manifest gets an image index
    // (or the reverse) and rejects it.
    const string indexAccept = UpstreamTestServer.IndexMediaType;
    const string manifestAccept = UpstreamTestServer.ManifestMediaType;
    const string path = "/v2/real-docker/testimage/manifests/24.04";

    myUpstreamServer.ManifestAcceptHeaders.Clear();
    var before = myUpstreamServer.ManifestByTagRequestCount;

    foreach (var (accept, expectedType, expectedBody) in new[]
             {
               (indexAccept, UpstreamTestServer.IndexMediaType, "{\"manifests\":[]}"),
               (manifestAccept, UpstreamTestServer.ManifestMediaType, "{\"layers\":[]}"),
             })
    {
      // Each representation misses on its first request and hits on its second, independently of the
      // other: two entries, not one shared one.
      foreach (var expectedStatus in new[] { CachingProxyStatus.MISS, CachingProxyStatus.HIT })
      {
        await AssertGetResponse(path, HttpStatusCode.OK,
          (message, bytes) =>
          {
            AssertStatusHeader(message, expectedStatus);
            Assert.Equal(expectedType, message.Content.Headers.ContentType?.ToString());
            Assert.Equal(expectedBody, Encoding.UTF8.GetString(bytes));
          },
          accept);
      }
    }

    // Two upstream fetches for four client requests, and each carried the client's own Accept - without
    // it Docker Hub answers with the legacy schema1 manifest that modern clients refuse.
    Assert.Equal(before + 2, myUpstreamServer.ManifestByTagRequestCount);
    Assert.Equal(new[] { indexAccept, manifestAccept }, myUpstreamServer.ManifestAcceptHeaders);
  }

  [Fact]
  public async Task Oci_Catalog_Is_Redirected()
  {
    // A registry-wide listing is dynamic, so it is bounced rather than cached.
    await AssertGetResponse("/v2/real-docker/_catalog", HttpStatusCode.RedirectKeepVerb,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
        Assert.Equal($"{myUpstreamServer.Url}v2/_catalog", message.Headers.Location?.ToString());
      });
  }

  [Fact]
  public async Task Oci_Registry_Token_Is_Minted_Once_And_Reused()
  {
    // A registry answers even an anonymous pull with 401 + a Bearer challenge and expects a token
    // fetched from the realm it names. Nothing about that is configured here: the challenge drives it.
    myUpstreamServer.RequireRegistryToken = true;
    var tokensBefore = myUpstreamServer.TokenRequestCount;
    try
    {
      await AssertGetResponse("/v2/real-docker/testimage/manifests/24.04", HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.MISS);
          Assert.Equal("{\"manifests\":[]}", Encoding.UTF8.GetString(bytes));
        },
        UpstreamTestServer.IndexMediaType);

      // A different resource in the same repository: the realm is remembered and the token is cached
      // under its scope, so it is attached up front - no second 401, no second mint.
      await AssertGetResponse($"/v2/real-docker/testimage/blobs/{UpstreamTestServer.BlobDigest}",
        HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.MISS);
          Assert.Equal("blob-content", Encoding.UTF8.GetString(bytes));
        });

      Assert.Equal(tokensBefore + 1, myUpstreamServer.TokenRequestCount);

      // The mint went out anonymously. No UpstreamAuth matches this prefix, and a client's own
      // credentials are never forwarded to an upstream or to its token endpoint: each upstream is
      // reached under its own service account or not at all.
      Assert.Equal("", myUpstreamServer.LastTokenRequestAuthorization);
    }
    finally
    {
      myUpstreamServer.RequireRegistryToken = false;
    }
  }

  [Fact]
  public async Task Content_Type_Is_The_Upstreams_Own()
  {
    // The upstream sends text/html for a .jar and that is relayed verbatim: the type is the upstream's
    // own, never derived from the path's extension. Deliberate — an OCI manifest path has no usable
    // extension and its media type is what the client reads the manifest schema off. It also matches
    // what the S3 backend has always done, so both backends agree.
    //
    // MISS streams it off the response; HIT reads it back from the entry's metadata; both must agree.
    foreach (var expectedStatus in new[] { CachingProxyStatus.MISS, CachingProxyStatus.HIT })
    {
      await AssertGetResponse("/real/wrong-content-type.jar", HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertStatusHeader(message, expectedStatus);
          Assert.Equal(MediaTypeNames.Text.Html, message.Content.Headers.ContentType?.ToString());
          Assert.Equal("some html", Encoding.UTF8.GetString(bytes));
        });
    }
  }

  [Fact]
  public async Task Content_Type_Falls_Back_To_Octet_Stream()
  {
    // This upstream route sends no Content-Type at all. MISS and HIT then both say octet-stream rather
    // than one of them saying nothing: on disk "absent" and "never sent" are the same thing, so the
    // fallback has to be the same on the way in and on the way out.
    foreach (var expectedStatus in new[] { CachingProxyStatus.MISS, CachingProxyStatus.HIT })
    {
      await AssertGetResponse("/real/artifact.pom", HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertStatusHeader(message, expectedStatus);
          Assert.Equal(MediaTypeNames.Application.Octet, message.Content.Headers.ContentType?.ToString());
          Assert.Equal("<project/>", Encoding.UTF8.GetString(bytes));
        });
    }
  }

  [Fact]
  public async Task Always_Cache_Directory_Index()
  {
    // A directory listing has no extension to derive a type from and the upstream serves it as HTML,
    // which is what gets relayed.
    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/",
      HttpStatusCode.OK, (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal(MediaTypeNames.Text.Html, message.Content.Headers.ContentType?.MediaType);
      });
  }

  [Fact]
  public async Task Always_Cache_Directory_Index_No_Trailing_Slash()
  {
    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz",
      HttpStatusCode.OK, (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal(MediaTypeNames.Text.Html, message.Content.Headers.ContentType?.MediaType);
      });
  }

  [Fact]
  public async Task Maven_Metadata_Is_Cached_With_Hourly_Freshness()
  {
    // maven-metadata.xml is mutable: the maven profile caches it (no longer an ALWAYS_REDIRECT) and
    // advertises its 1-hour freshness window as the client max-age.
    await AssertGetResponse("/real/group/artifact/maven-metadata.xml", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("public, max-age=3600", message.Headers.CacheControl?.ToString());
        Assert.Equal("<metadata><versioning/></metadata>", Encoding.UTF8.GetString(bytes));
      });

    await AssertGetResponse("/real/group/artifact/maven-metadata.xml", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal("public, max-age=3600", message.Headers.CacheControl?.ToString());
      });
  }

  [Fact]
  public async Task Maven_Per_Endpoint_Freshness_Windows_Differ()
  {
    // Prime both endpoints (MISS), then advance the clock past the metadata window (1h) but not the
    // snapshot window (1 day): the metadata revalidates while the snapshot is still a plain HIT — proof
    // the freshness window is resolved per endpoint, not globally.
    await AssertGetResponse("/real/group/artifact/maven-metadata.xml", HttpStatusCode.OK,
      (m, _) => AssertStatusHeader(m, CachingProxyStatus.MISS));
    await AssertGetResponse("/real/group/artifact/1.0-SNAPSHOT/artifact-1.0-SNAPSHOT.jar", HttpStatusCode.OK,
      (m, _) => AssertStatusHeader(m, CachingProxyStatus.MISS));

    myTimeProvider.Advance(TimeSpan.FromHours(2));

    // Metadata is stale (> 1h): revalidated against the upstream, which answers 304 => kept and served.
    await AssertGetResponse("/real/group/artifact/maven-metadata.xml", HttpStatusCode.OK,
      (m, bytes) =>
      {
        AssertStatusHeader(m, CachingProxyStatus.REVALIDATED);
        Assert.Equal("<metadata><versioning/></metadata>", Encoding.UTF8.GetString(bytes));
      });

    // The snapshot window (1 day) has not elapsed: still a plain HIT, with no upstream revalidation.
    var snapshotHitsBefore = myUpstreamServer.SnapshotRequestCount;
    await AssertGetResponse("/real/group/artifact/1.0-SNAPSHOT/artifact-1.0-SNAPSHOT.jar", HttpStatusCode.OK,
      (m, _) => AssertStatusHeader(m, CachingProxyStatus.HIT));
    Assert.Equal(snapshotHitsBefore, myUpstreamServer.SnapshotRequestCount);
  }

  [Fact]
  public async Task No_Route_To_Host()
  {
    // https://en.wikipedia.org/wiki/Reserved_IP_addresses
    // 198.51.100.0/24 reserved for documentation
    await AssertGetResponse("/198.51.100.9/a.txt", HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
        var cachedStatus = message.Headers.GetValues(CachingProxyConstants.CachedStatusHeader).First();
        Assert.True(cachedStatus == "503" || cachedStatus == "504",
          $"cached status should be 503 or 504: {cachedStatus}");
      });

    await AssertGetResponse("/198.51.100.9/a.txt", HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
        var cachedStatus = message.Headers.GetValues(CachingProxyConstants.CachedStatusHeader).First();
        Assert.True(cachedStatus == "503" || cachedStatus == "504",
          $"cached status should be 503 or 504: {cachedStatus}");
      });
  }

  [Fact]
  public async Task Unknown_Host()
  {
    await AssertGetResponse("/unknown_host.xyz/a.txt", HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
        AssertCachedStatusHeader(message, HttpStatusCode.ServiceUnavailable);
      });

    await AssertGetResponse("/unknown_host.xyz/a.txt", HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
        AssertCachedStatusHeader(message, HttpStatusCode.ServiceUnavailable);
      });
  }

  [Fact]
  public async Task Remote_NotFound()
  {
    await AssertGetResponse("/repo1.maven.org/maven2/not_found.txt", HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
        AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        Assert.Null(message.Headers.CacheControl);
      });

    await AssertGetResponse("/repo1.maven.org/maven2/not_found.txt", HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
        AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        Assert.Null(message.Headers.CacheControl);
      });
  }

  [Fact]
  public async Task Remote_Wrong_Content_Length()
  {
    await Assert.ThrowsAsync<HttpRequestException>(async () =>
    {
      await AssertGetResponse("/real/wrong-content-length.jar", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
          AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        });
    });
  }

  [Fact]
  public async Task Remote_InternalError()
  {
    await AssertGetResponse("/real/500.jar", HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
        AssertCachedStatusHeader(message, HttpStatusCode.InternalServerError);
        Assert.Null(message.Headers.CacheControl);
      });

    await AssertGetResponse("/real/500.jar", HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
        AssertCachedStatusHeader(message, HttpStatusCode.InternalServerError);
        Assert.Null(message.Headers.CacheControl);
      });
  }

  [Theory]
  [InlineData("/real/401.jar", HttpStatusCode.Unauthorized)]
  [InlineData("/real/402.jar", HttpStatusCode.PaymentRequired)]
  [InlineData("/real/403.jar", HttpStatusCode.Forbidden)]
  public async Task Remote_AuthErrors_AreSurfacedVerbatim_AndNeverNegativelyCached(string url, HttpStatusCode status)
  {
    // Authentication / access errors must be surfaced to the client verbatim (not masked to 404).
    // They are also never negatively cached (their cache duration is zero): every request re-probes
    // upstream, so both requests are live NEGATIVE_MISS responses and neither carries the
    // Cached-Status header (the entry is never stored).
    for (var i = 0; i < 2; i++)
      await AssertGetResponse(url, status,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
          AssertNoCachedStatusHeader(message);
          Assert.Null(message.Headers.CacheControl);
        });
  }

  [Fact]
  public async Task More_Specific_Prefix_Wins_Over_Shorter_Overlapping_One()
  {
    // /overlap/nested/a.jar must be served by the more specific "/overlap/nested" prefix (-> upstream
    // "a.jar", body "a.jar"), NOT by the shorter "/overlap" prefix (-> upstream "wrong/nested/a.jar",
    // which 404s). This holds even though "/overlap" is declared first in the config.
    await AssertGetResponse("/overlap/nested/a.jar", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("a.jar", Encoding.UTF8.GetString(bytes));
      });

    // A path that only matches the shorter prefix is still routed there (-> upstream "wrong/a.jar",
    // which 404s), confirming the shorter prefix remains active for its own paths.
    await AssertGetResponse("/overlap/a.jar", HttpStatusCode.NotFound,
      (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS));
  }

  [Fact]
  public async Task Unknown_Prefix()
  {
    await AssertGetResponse("/some_unknown_prefix/a.txt", HttpStatusCode.NotFound,
      (message, bytes) => { AssertNoStatusHeader(message); });
  }

  [Fact]
  public async Task UserAgent()
  {
    await myServer.CreateRequest("/real/a.html").GetAsync();
    var agent = myUpstreamServer.LastUserAgent;
    myOutput.WriteLine("*** UserAgent: " + agent);
    Assert.StartsWith(typeof(ProxyHttpClient).Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product, agent);
    Assert.EndsWith(myConfig.UserAgentComment, agent);
  }

  [Fact]
  public async Task Custom_Cache_Duration_Prefix_Extends_Head_Positive_Cache()
  {
    // Custom OK duration on this prefix is 30 minutes (default for OK is 5 minutes).
    // HEAD is used because GET would persist the file to disk and StaticFiles would
    // serve subsequent requests regardless of the in-memory cache state.
    const string url = "/real-custom-ttl/gzipEncoding.txt";
    var customOkDuration = TimeSpan.FromMinutes(30);

    myTimeProvider.AdjustTime(myTimeProvider.Start);

    await AssertHeadResponse(url, HttpStatusCode.OK,
      message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        AssertCachedStatusHeader(message, HttpStatusCode.OK);
        AssertCachedUntilHeader(message, myTimeProvider.GetUtcNow() + customOkDuration);
      });

    // Past the 5-minute default but still within the custom 30-minute window.
    // Cached-Until reflects the original MISS time (Start), not the time of this HIT.
    myTimeProvider.Advance(TimeSpan.FromMinutes(10));
    await AssertHeadResponse(url, HttpStatusCode.OK,
      message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        AssertCachedStatusHeader(message, HttpStatusCode.OK);
        AssertCachedUntilHeader(message, myTimeProvider.Start + customOkDuration);
      });

    // Past the 30-minute custom window — entry should be evicted.
    myTimeProvider.Advance(TimeSpan.FromMinutes(25));
    await AssertHeadResponse(url, HttpStatusCode.OK,
      message =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        AssertCachedStatusHeader(message, HttpStatusCode.OK);
        AssertCachedUntilHeader(message, myTimeProvider.GetUtcNow() + customOkDuration);
      });
  }

  [Fact]
  public async Task Custom_Cache_Duration_Prefix_Extends_Negative_Cache()
  {
    // Custom NotFound duration on this prefix is 15 minutes (default is 5 minutes).
    const string url = "/real-custom-ttl/not_found.txt";
    var customNotFoundDuration = TimeSpan.FromMinutes(15);

    myTimeProvider.AdjustTime(myTimeProvider.Start);

    await AssertGetResponse(url, HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
        AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        AssertCachedUntilHeader(message, myTimeProvider.GetUtcNow() + customNotFoundDuration);
      });

    // Past the 5-minute default but within the custom 15-minute window.
    // Cached-Until reflects the original MISS time (Start), not the time of this HIT.
    myTimeProvider.Advance(TimeSpan.FromMinutes(10));
    await AssertGetResponse(url, HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
        AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        AssertCachedUntilHeader(message, myTimeProvider.Start + customNotFoundDuration);
      });

    // Past the 15-minute custom window — negative cache entry should be evicted.
    myTimeProvider.Advance(TimeSpan.FromMinutes(10));
    await AssertGetResponse(url, HttpStatusCode.NotFound,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
        AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        AssertCachedUntilHeader(message, myTimeProvider.GetUtcNow() + customNotFoundDuration);
      });
  }

  [Fact]
  public async Task CleanupService()
  {
    myTimeProvider.AdjustTime(myTimeProvider.Start);

    foreach (var directory in Directory.EnumerateDirectories(myConfig.LocalCachePath, "*", SearchOption.TopDirectoryOnly))
    {
      Directory.Delete(directory, true);
    }
    await myServer.CreateRequest("/real/a.jar").GetAsync();
    var cachedFile = Assert.Single(GetArtifacts());
    File.SetLastAccessTimeUtc(cachedFile, myTimeProvider.Start.UtcDateTime);
    // Every stored copy also gets a metadata companion (see CacheEntryMetadata).
    Assert.True(File.Exists(CacheFileProvider.GetMetadataPath(cachedFile)));

    // Within the retention period: advancing by half of it must not delete the freshly-accessed file.
    // Its companion survives too - the cutoff does not apply to one whose artifact is still there.
    myTimeProvider.Advance(myConfig.CleanupPeriod / 2);
    await DrainAsync();
    Assert.Single(GetArtifacts());
    Assert.True(File.Exists(CacheFileProvider.GetMetadataPath(cachedFile)));

    // Past the retention period: the cron-driven cleanup runs on a background task whose cutoff is
    // computed from GetUtcNow() when its FakeTimeProvider timer continuation runs. Advance a cron
    // period at a time and let that continuation run, polling until the now-expired file is deleted.
    // Polling (rather than a single fixed delay) keeps this independent of the wall-clock hour the
    // test starts at and of the timer-scheduling race between advances.
    for (var i = 0; i < 10 && GetFiles().Any(); i++)
    {
      myTimeProvider.Advance(myConfig.CleanupPeriod);
      await DrainAsync();
    }
    // Both the artifact and the companion it orphaned are gone. The companion may need a second pass:
    // within one pass it is exempt while its artifact is still on disk, whichever order they are visited.
    Assert.Empty(GetFiles());
    return;

    IEnumerable<string> GetFiles() =>
      Directory.EnumerateFiles(myConfig.LocalCachePath, "*", SearchOption.AllDirectories);

    IEnumerable<string> GetArtifacts() => GetFiles().Where(f => !CacheFileProvider.IsMetadata(f));

    // Yield repeatedly so the background cleanup loop's continuation can run after a clock change.
    static async Task DrainAsync()
    {
      for (var i = 0; i < 20; i++)
        await Task.Delay(TimeSpan.FromMilliseconds(25));
    }
  }

  // Builds a second proxy host over the same cache directory and fake clock, with a profile whose
  // catch-all rule gives every path the requested freshness window — so the freshness/revalidation
  // behavior can be exercised on /real/revalidate.txt without touching the other tests.
  private TestServer CreateRefreshAfterServer(TimeSpan refreshAfter)
  {
    var config = new CachingProxyConfig
    {
      LocalCachePath = myTempDirectory,
      CachingProfiles = new Dictionary<string, CachingProfile>
      {
        ["fresh"] = new() { Rules = [new CachingRule { Pattern = ".", RefreshAfter = refreshAfter }] }
      },
      Prefixes = [new CachingProxyPrefix($"/real={myUpstreamServer.Url}", Profile: "fresh")],
      MinimumFreeDiskSpaceMb = 2,
    };

    var host = new HostBuilder()
      .ConfigureWebHost(webHostBuilder => webHostBuilder
        .UseTestServer()
        .ConfigureAppConfiguration(cfg =>
          cfg.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(config))))
        .ConfigureOurServices()
        .ConfigureServices(services => services
          .AddSingleton(config)
          .Replace(ServiceDescriptor.Singleton<TimeProvider>(myTimeProvider)))
        .Configure((context, builder) => builder.ConfigureOurApp(context.Configuration)))
      .Build();

    myExtraHosts.Add(host);
    host.Start();
    return host.GetTestServer();
  }

  private IEnumerable<string> CacheFiles() =>
    // Exclude the Data Protection key ring persisted under the cache dir — only count cache artifacts.
    Directory.EnumerateFiles(myTempDirectory, "*", SearchOption.AllDirectories)
      .Where(f => !f.Contains(".dataprotection-keys"));

  [Fact]
  public async Task Cache_Control_Max_Age_Reflects_RefreshAfter()
  {
    // With a freshness window configured, the served Cache-Control max-age advertises that window
    // (not the eternal 365-day default) so downstream caches revalidate in step with the proxy.
    var server = CreateRefreshAfterServer(TimeSpan.FromMinutes(5));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    myUpstreamServer.RevalidateContent = "v1";

    using (var miss = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
      AssertStatusHeader(miss, CachingProxyStatus.MISS);
      Assert.Equal("public, max-age=300", miss.Headers.CacheControl?.ToString());
    }

    // A plain HIT served from disk advertises the same window.
    using var hit = await server.CreateRequest("/real/revalidate.txt").GetAsync();
    AssertStatusHeader(hit, CachingProxyStatus.HIT);
    Assert.Equal("public, max-age=300", hit.Headers.CacheControl?.ToString());
  }

  [Fact]
  public async Task Stale_Upstream_NotModified_Is_Kept_And_Window_Resets()
  {
    var server = CreateRefreshAfterServer(TimeSpan.FromMinutes(1));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    myUpstreamServer.RevalidateContent = "v1";

    using (var miss = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
      AssertStatusHeader(miss, CachingProxyStatus.MISS);
      Assert.Equal("v1", await miss.Content.ReadAsStringAsync());
    }

    // Past the window, upstream reports 304: keep & serve the stored copy and reset the window.
    myTimeProvider.Advance(TimeSpan.FromMinutes(2));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotModified;
    using (var reval = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      Assert.Equal(HttpStatusCode.OK, reval.StatusCode);
      AssertStatusHeader(reval, CachingProxyStatus.REVALIDATED);
      Assert.Equal("v1", await reval.Content.ReadAsStringAsync());
    }

    // Window was reset by the touch, so an immediate request is a plain HIT with no upstream call.
    var upstreamHitsBefore = myUpstreamServer.RevalidateRequestCount;
    using (var hit = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
      AssertStatusHeader(hit, CachingProxyStatus.HIT);
      Assert.Equal("v1", await hit.Content.ReadAsStringAsync());
    }
    Assert.Equal(upstreamHitsBefore, myUpstreamServer.RevalidateRequestCount);
  }

  [Fact]
  public async Task Stale_Upstream_Changed_Is_Replaced()
  {
    var server = CreateRefreshAfterServer(TimeSpan.FromMinutes(1));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    myUpstreamServer.RevalidateContent = "v1";

    using (var miss = await server.CreateRequest("/real/revalidate.txt").GetAsync())
      AssertStatusHeader(miss, CachingProxyStatus.MISS);

    // Past the window, upstream returns new content (200): replace the stored copy and serve it.
    myTimeProvider.Advance(TimeSpan.FromMinutes(2));
    myUpstreamServer.RevalidateContent = "v2-bigger";
    using (var reval = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      Assert.Equal(HttpStatusCode.OK, reval.StatusCode);
      AssertStatusHeader(reval, CachingProxyStatus.REVALIDATED);
      Assert.Equal("v2-bigger", await reval.Content.ReadAsStringAsync());
    }

    // The replaced copy is fresh: an immediate request is a HIT serving the new content.
    using var hit = await server.CreateRequest("/real/revalidate.txt").GetAsync();
    AssertStatusHeader(hit, CachingProxyStatus.HIT);
    Assert.Equal("v2-bigger", await hit.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task Fresh_Window_Is_Anchored_To_Our_Stored_Date_Not_Upstream_Last_Modified()
  {
    // The freshness window must be measured from when *we* stored the copy, never from a timestamp the
    // upstream controls. Real upstreams report a Last-Modified in the past (it is preserved as the
    // cache file's own write time so the served header survives), so anchoring the window to a file
    // timestamp the upstream can drag backwards would make every stored copy born stale and revalidate
    // on every single request — silently turning RefreshAfter into "no caching at all".
    var server = CreateRefreshAfterServer(TimeSpan.FromMinutes(10));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    myUpstreamServer.RevalidateContent = "v1";
    myUpstreamServer.RevalidateLastModified = "Tue, 10 Jul 2018 04:58:42 GMT";

    using (var miss = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      AssertStatusHeader(miss, CachingProxyStatus.MISS);
      Assert.Equal("Tue, 10 Jul 2018 04:58:42 GMT", miss.Content.Headers.GetValues("Last-Modified").Single());
    }

    // Well inside the window: a plain HIT that never reaches the upstream.
    var upstreamHitsBefore = myUpstreamServer.RevalidateRequestCount;
    using (var hit = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      AssertStatusHeader(hit, CachingProxyStatus.HIT);
      Assert.Equal("v1", await hit.Content.ReadAsStringAsync());
    }
    Assert.Equal(upstreamHitsBefore, myUpstreamServer.RevalidateRequestCount);
  }

  [Fact]
  public async Task Revalidated_Copy_Keeps_Serving_Upstream_Last_Modified()
  {
    // Resetting the window must not disturb the artifact's own timestamps. The served Last-Modified —
    // and the ETag PhysicalFile derives from it — has to survive a 304 keep, otherwise every window
    // reset would invalidate downstream caches and make them re-download an unchanged body.
    var server = CreateRefreshAfterServer(TimeSpan.FromMinutes(1));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    myUpstreamServer.RevalidateContent = "v1";
    myUpstreamServer.RevalidateLastModified = "Tue, 10 Jul 2018 04:58:42 GMT";

    using (var miss = await server.CreateRequest("/real/revalidate.txt").GetAsync())
      AssertStatusHeader(miss, CachingProxyStatus.MISS);

    myTimeProvider.Advance(TimeSpan.FromMinutes(2));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotModified;

    string? revalidatedETag;
    using (var reval = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      AssertStatusHeader(reval, CachingProxyStatus.REVALIDATED);
      Assert.Equal("Tue, 10 Jul 2018 04:58:42 GMT", reval.Content.Headers.GetValues("Last-Modified").Single());
      revalidatedETag = reval.Headers.ETag?.ToString();
    }

    // And the following HIT is byte-for-byte the same entity as far as a downstream cache can tell.
    using var hit = await server.CreateRequest("/real/revalidate.txt").GetAsync();
    AssertStatusHeader(hit, CachingProxyStatus.HIT);
    Assert.Equal("Tue, 10 Jul 2018 04:58:42 GMT", hit.Content.Headers.GetValues("Last-Modified").Single());
    Assert.Equal(revalidatedETag, hit.Headers.ETag?.ToString());
  }

  [Fact]
  public async Task Stale_Upstream_Error_Serves_Stale_Copy()
  {
    var server = CreateRefreshAfterServer(TimeSpan.FromMinutes(1));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    myUpstreamServer.RevalidateContent = "v1";

    using (var miss = await server.CreateRequest("/real/revalidate.txt").GetAsync())
      AssertStatusHeader(miss, CachingProxyStatus.MISS);

    // Past the window, upstream fails: keep and still serve the stale copy.
    myTimeProvider.Advance(TimeSpan.FromMinutes(2));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.ServerError;
    using (var stale = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
      AssertStatusHeader(stale, CachingProxyStatus.STALE);
      Assert.Equal("v1", await stale.Content.ReadAsStringAsync());
    }

    Assert.NotEmpty(CacheFiles()); // the stale copy was kept on disk
  }

  [Fact]
  public async Task Stale_Upstream_NotFound_Deletes_Cache()
  {
    var server = CreateRefreshAfterServer(TimeSpan.FromMinutes(1));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    myUpstreamServer.RevalidateContent = "v1";

    using (var miss = await server.CreateRequest("/real/revalidate.txt").GetAsync())
      AssertStatusHeader(miss, CachingProxyStatus.MISS);
    Assert.NotEmpty(CacheFiles());

    // Past the window, upstream is gone (404): delete the stale copy and return 404.
    myTimeProvider.Advance(TimeSpan.FromMinutes(2));
    myUpstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotFound;
    using (var gone = await server.CreateRequest("/real/revalidate.txt").GetAsync())
    {
      Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
      AssertStatusHeader(gone, CachingProxyStatus.NEGATIVE_MISS);
    }

    Assert.Empty(CacheFiles()); // the stale copy was removed
  }

  private async Task AssertGetResponse(string url, HttpStatusCode expectedCode,
    Action<HttpResponseMessage, byte[]> assertions, string? accept = null)
  {
    myOutput.WriteLine($"*** GET {url}{(accept == null ? "" : $" (Accept: {accept})")}");
    var request = myServer.CreateRequest(url);
    if (accept != null) request.AddHeader("Accept", accept);
    using var response = await request.GetAsync();
    var bytes = await response.Content.ReadAsByteArrayAsync();

    myOutput.WriteLine(response.ToString());
    if (bytes.All(c => c < 128) && bytes.Length < 200)
      myOutput.WriteLine("Body: " + Encoding.UTF8.GetString(bytes));

    Assert.Equal(expectedCode, response.StatusCode);
    assertions(response, bytes);
  }

  private async Task AssertHeadResponse(string url, HttpStatusCode expectedCode, Action<HttpResponseMessage> assertions)
  {
    myOutput.WriteLine("*** HEAD " + url);
    using var response = await myServer.CreateRequest(url).SendAsync(HttpMethod.Head.Method);
    myOutput.WriteLine(response.ToString());
    Assert.Equal(expectedCode, response.StatusCode);
    assertions(response);
  }

  private async Task AssertPostResponse(string url, HttpStatusCode expectedCode, Action<HttpResponseMessage> assertions)
  {
    myOutput.WriteLine("*** POST " + url);
    using var response = await myServer.CreateRequest(url).SendAsync(HttpMethod.Post.Method);
    myOutput.WriteLine(response.ToString());
    Assert.Equal(expectedCode, response.StatusCode);
    assertions(response);
  }

  private static long? GetContentLength(HttpResponseMessage response)
  {
    var values = response.Content.Headers.FirstOrDefault(x => x.Key == "Content-Length").Value;
    if (values == null) return null;
    return long.Parse(values.First());
  }

  private static void AssertStatusHeader(HttpResponseMessage response, CachingProxyStatus status)
  {
    var statusHeader = response.Headers.GetValues(CachingProxyConstants.StatusHeader).FirstOrDefault();
    Assert.Equal(status.ToString(), statusHeader);
  }

  private static void AssertCachedStatusHeader(HttpResponseMessage response, HttpStatusCode status)
  {
    var statusHeader = response.Headers.GetValues(CachingProxyConstants.CachedStatusHeader).FirstOrDefault();
    Assert.Equal(((int) status).ToString(), statusHeader);
  }

  private static void AssertCachedUntilHeader(HttpResponseMessage response, DateTimeOffset expected)
  {
    var untilHeader = response.Headers.GetValues(CachingProxyConstants.CachedUntilHeader).FirstOrDefault();
    Assert.Equal(expected.ToString("R"), untilHeader);
  }

  private static void AssertNoStatusHeader(HttpResponseMessage response)
  {
    if (response.Headers.TryGetValues(CachingProxyConstants.StatusHeader, out var headers))
    {
      throw new Exception($"Expected no {CachingProxyConstants.StatusHeader} header, but got: " + string.Join(", ", headers));
    }
  }

  private static void AssertNoCachedStatusHeader(HttpResponseMessage response)
  {
    if (response.Headers.TryGetValues(CachingProxyConstants.CachedStatusHeader, out var headers))
    {
      throw new Exception($"Expected no {CachingProxyConstants.CachedStatusHeader} header, but got: " + string.Join(", ", headers));
    }
  }

  // ReSharper disable once InconsistentNaming
  private static string SHA256(byte[] input)
  {
    var hash = System.Security.Cryptography.SHA256.HashData(input);
    return string.Join("", hash.Select(b => b.ToString("x2")).ToArray());
  }

  Task IAsyncLifetime.InitializeAsync()
  {
    Environment.SetEnvironmentVariable("SENTRY_RELEASE", "release@1.0.0");
    return myHost.StartAsync();
  }

  async Task IAsyncLifetime.DisposeAsync()
  {
    foreach (var host in myExtraHosts)
      await host.StopAsync();
    await myHost.StopAsync();
    Directory.Delete(myTempDirectory, true);
  }
}
