using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace JetBrains.CachingProxy.Tests
{
  // TODO: Negative caching expiration per status code
  // TODO: Switch to real server in tests
  public class CachingProxyTest : IAsyncLifetime, IClassFixture<UpstreamTestServer>
  {
    private readonly ITestOutputHelper myOutput;
    private readonly IHost myServer;
    private readonly string myTempDirectory;
    private readonly UpstreamTestServer myUpstreamServer;

    public CachingProxyTest(ITestOutputHelper output, UpstreamTestServer upstreamServer)
    {
      myOutput = output;
      myTempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
      Directory.CreateDirectory(myTempDirectory);

      var config = new CachingProxyConfig
      {
        LocalCachePath = myTempDirectory,
        Prefixes =
        [
          "/repo1.maven.org/maven2",
          "/198.51.100.9",
          "/plugins.gradle.org/m2",
          "/unknown_host.xyz",
          $"/real={upstreamServer.Url}"
        ],
        ContentTypeValidationPrefixes =
        [
          "/real"
        ],
        MinimumFreeDiskSpaceMb = 2,
      };

      myServer = new HostBuilder()
        .ConfigureWebHost(webHostBuilder =>
        {
          webHostBuilder
            .UseTestServer()
            .ConfigureTestServices(services =>
            {
              services.Add(new ServiceDescriptor(typeof(IOptions<CachingProxyConfig>),
                new OptionsWrapper<CachingProxyConfig>(config)));
              Program.ConfigureOurServices(services);
            })
            .Configure(app => app.UseMiddleware<CachingProxy>());
        })
        .Build();

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
        Path.Combine(myTempDirectory, "repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/cache-ant-xz-1.10.5.jar")).Length);
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
        Assert.Equal($"{myUpstreamServer.Url}/fakeBrEncoding.txt returned Content-Encoding 'br' which is not supported", Encoding.UTF8.GetString(bytes));
      });
    }

    [Fact]
    public async Task Multiple_Encodings_Are_Not_Supported_In_Content_Encoding()
    {
      await AssertGetResponse("/real/fakeMultipleEncodings.txt", HttpStatusCode.ServiceUnavailable, (message, bytes) =>
      {
        Assert.Equal($"{myUpstreamServer.Url}/fakeMultipleEncodings.txt returned multiple Content-Encoding which is not allowed: deflate, gzip", Encoding.UTF8.GetString(bytes));
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
      // npm scoped packages use %2f in registry URLs (e.g. @types%2fserve-index)
      // Kestrel preserves %2f in Request.Path, so use extensionless path to trigger ALWAYS_REDIRECT
      await AssertGetResponse("/real/@scope%2fpackage", HttpStatusCode.TemporaryRedirect,
        (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT));
    }

    [Fact]
    public async Task Retry_After_500()
    {
      myUpstreamServer.Conditional500SendErrorOnce = true;
      await AssertGetResponse("/real/conditional-500.txt", HttpStatusCode.OK, (message, bytes) => AssertStatusHeader(message, CachingProxyStatus.MISS));
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

      using var response1 = myServer.GetTestClient().GetAsync(url);
      using var response2 = myServer.GetTestClient().GetAsync(url);

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
        HttpStatusCode.TemporaryRedirect,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
          Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz/1.0-SNAPSHOT/ant-xz-1.0-SNAPSHOT.jar",
            message.Headers.Location?.ToString());
        });
    }

    [Fact]
    public async Task Always_Redirect_Directory_Index()
    {
      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/",
        HttpStatusCode.TemporaryRedirect,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
          Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz/",
            message.Headers.Location?.ToString());
        });
    }

    [Fact]
    public async Task Always_Redirect_Directory_Index_No_Trailing_Slash()
    {
      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz",
        HttpStatusCode.TemporaryRedirect,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
          Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz",
            message.Headers.Location?.ToString());
        });
    }

    [Fact]
    public async Task Always_Redirect_MavenMetadataXml()
    {
      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/maven-metadata.xml",
        HttpStatusCode.TemporaryRedirect,
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
        HttpStatusCode.TemporaryRedirect,
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
    public async Task Remote_WrongContentType()
    {
      await AssertGetResponse("/real/wrong-content-type.jar", HttpStatusCode.ServiceUnavailable,
        (message, bytes) =>
        {
          var text = Encoding.UTF8.GetString(bytes);
          Assert.Equal($"{myUpstreamServer.Url}/wrong-content-type.jar returned content type 'text/html' which is forbidden by content type validation for file extension '.jar'", text);
        });
    }

    [Fact]
    public async Task Unknown_Prefix()
    {
      await AssertGetResponse("/some_unknown_prefix/a.txt", HttpStatusCode.NotFound,
        (message, bytes) => { AssertNoStatusHeader(message); });
    }

    private async Task AssertGetResponse(string url, HttpStatusCode expectedCode,
      Action<HttpResponseMessage, byte[]> assertions)
    {
      myOutput.WriteLine("*** GET " + url);
      using var response = await myServer.GetTestClient().GetAsync(url);
      var bytes = await response.Content.ReadAsByteArrayAsync();

      myOutput.WriteLine(response.ToString());
      if (bytes.All(c => c < 128) && bytes.Length < 200)
        myOutput.WriteLine("Body: " + Encoding.UTF8.GetString(bytes));

      Assert.Equal(expectedCode, response.StatusCode);
      assertions(response, bytes);
    }

    private async Task AssertHeadResponse(string url, HttpStatusCode expectedCode,
      Action<HttpResponseMessage> assertions)
    {
      myOutput.WriteLine("*** HEAD " + url);
      using var response = await myServer.GetTestClient().SendAsync(
        new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseContentRead);
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
      var hash = System.Security.Cryptography.SHA256.Create().ComputeHash(input);
      return string.Join("", hash.Select(b => b.ToString("x2")).ToArray());
    }

    Task IAsyncLifetime.InitializeAsync()
    {
      return myServer.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
      await myServer.StopAsync();
      Directory.Delete(myTempDirectory, true);
    }
  }
}
