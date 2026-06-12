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

  public CachingProxyTest(ITestOutputHelper output, UpstreamTestServer upstreamServer)
  {
    myOutput = output;
    myTempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(myTempDirectory);

    myConfig = new CachingProxyConfig
    {
      LocalCachePath = myTempDirectory,
      Prefixes =
      [
        "/repo1.maven.org/maven2",
        "/198.51.100.9",
        "/plugins.gradle.org/m2",
        "/registry.npmjs.org",
        "/unknown_host.xyz",
        $"/real={upstreamServer.Url}",
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

    myTimeProvider = new FakeTimeProvider();
    myHost = new HostBuilder()
      .ConfigureWebHost(webHostBuilder =>
      {
        webHostBuilder
          .UseTestServer()
          .ConfigureAppConfiguration(cfg =>
            cfg.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(myConfig))))
          .ConfigureServices((context, services) =>
          {
            services
              .AddSingleton(myConfig)
              .ConfigureOurServices(context.Configuration)
              .Replace(ServiceDescriptor.Singleton<TimeProvider>(myTimeProvider));
          })
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
        Assert.Equal("HealthCheck: release@1.0.0\nCachingProxy: Healthy\n", Encoding.UTF8.GetString(bytes));
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
  public async Task Always_Redirect_Snapshots()
  {
    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.0-SNAPSHOT/ant-xz-1.0-SNAPSHOT.jar",
      HttpStatusCode.RedirectKeepVerb,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
        Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz/1.0-SNAPSHOT/ant-xz-1.0-SNAPSHOT.jar",
          message.Headers.Location?.ToString());
      });
  }

  [Fact]
  public async Task Always_Redirect_Npm_Security_Check()
  {
    await AssertGetResponse("/registry.npmjs.org/-/npm/v1/security/audits/quick",
      HttpStatusCode.RedirectKeepVerb,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
        Assert.Equal("https://registry.npmjs.org/-/npm/v1/security/audits/quick",
          message.Headers.Location?.ToString());
      });
  }

  [Fact]
  public async Task Content_Type_Is_Produced_From_Extension()
  {
    // The upstream sends text/html for a .jar, but the proxy derives the Content-Type from the file
    // extension. The type is therefore application/java-archive on both MISS and HIT, so a response
    // streamed on a MISS and the same artifact served from disk on a HIT agree.
    await AssertGetResponse("/real/wrong-content-type.jar", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal("application/java-archive", message.Content.Headers.ContentType?.ToString());
        Assert.Equal("some html", Encoding.UTF8.GetString(bytes));
      });

    await AssertGetResponse("/real/wrong-content-type.jar", HttpStatusCode.OK,
      (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.HIT);
        Assert.Equal("application/java-archive", message.Content.Headers.ContentType?.ToString());
        Assert.Equal("some html", Encoding.UTF8.GetString(bytes));
      });
  }

  [Fact]
  public async Task Always_Cache_Directory_Index()
  {
    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/",
      HttpStatusCode.OK, (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal(MediaTypeNames.Application.Octet, message.Content.Headers.ContentType?.ToString());
      });
  }

  [Fact]
  public async Task Always_Cache_Directory_Index_No_Trailing_Slash()
  {
    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz",
      HttpStatusCode.OK, (message, bytes) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.MISS);
        Assert.Equal(MediaTypeNames.Application.Octet, message.Content.Headers.ContentType?.ToString());
      });
  }

  [Fact]
  public async Task Always_Redirect_MavenMetadataXml()
  {
    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/maven-metadata.xml",
      HttpStatusCode.RedirectKeepVerb,
      (message, _) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
        Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz/maven-metadata.xml",
          message.Headers.Location?.ToString());
      });
  }

  [Fact]
  public async Task Always_Redirect_MavenMetadataXmlChecksum()
  {
    await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/maven-metadata.xml.sha1",
      HttpStatusCode.RedirectKeepVerb,
      (message, _) =>
      {
        AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
        Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz/maven-metadata.xml.sha1",
          message.Headers.Location?.ToString());
      });
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
    var cachedFile = Assert.Single(GetFiles());
    File.SetLastAccessTimeUtc(cachedFile, myTimeProvider.Start.UtcDateTime);
    myTimeProvider.Advance(myConfig.CleanupPeriod / 2);
    Assert.Single(GetFiles());
    myTimeProvider.Advance(myConfig.CleanupPeriod / 2 + TimeSpan.FromSeconds(1));
    await Task.Delay(TimeSpan.FromSeconds(1)); // wait for file deletion
    Assert.Empty(GetFiles());
    return;
    IEnumerable<string> GetFiles() =>
      Directory.EnumerateFiles(myConfig.LocalCachePath, "*", SearchOption.AllDirectories);
  }

  private async Task AssertGetResponse(string url, HttpStatusCode expectedCode, Action<HttpResponseMessage, byte[]> assertions)
  {
    myOutput.WriteLine("*** GET " + url);
    using var response = await myServer.CreateRequest(url).GetAsync();
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
    await myHost.StopAsync();
    Directory.Delete(myTempDirectory, true);
  }
}
