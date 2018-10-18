using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace JetBrains.CachingProxy.Tests
{
  // TODO: Test with wrong content-length from remote side
  // TODO: Test remote 5xx
  // TODO: Negative caching expiration
  // TODO: Test hierarchy conflicts like caching both `a/a.jar` and `a/a.jar/b.jar`
  public class CachingProxyTest : IDisposable
  {
    private readonly ITestOutputHelper myOutput;
    private readonly TestServer myServer;
    private readonly string myTempDirectory;

    public CachingProxyTest(ITestOutputHelper output)
    {
      myOutput = output;
      myTempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
      Directory.CreateDirectory(myTempDirectory);

      var config = new CachingProxyConfig
      {
        LocalCachePath = myTempDirectory,
        Prefixes = new[]
        {
          "/repo1.maven.org/maven2",
          "/198.51.100.9",
          "/plugins.gradle.org/m2",
          "/unknown_host.xyz"
        }
      };

      var builder = new WebHostBuilder()
        .ConfigureServices(services => services.Add(new ServiceDescriptor(typeof(IOptions<CachingProxyConfig>),
          new OptionsWrapper<CachingProxyConfig>(config))))
        .Configure(app => { app.UseMiddleware<CachingProxy>(); }
        );

      myServer = new TestServer(builder);
    }

    [Fact]
    public async void Caching_Works()
    {
      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.MISS);
          Assert.Equal("application/java-archive", message.Content.Headers.ContentType.ToString());
          Assert.Equal(11541, GetContentLength(message));
          Assert.Equal(11541, bytes.Length);
          Assert.Equal("eca06bb19a4f55673f8f40d0a20eb0ee0342403ee5856b890d6c612e5facb027", SHA256(bytes));
        });

      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertNoStatusHeader(message);
          Assert.Equal(11541, GetContentLength(message));
          Assert.Equal("application/java-archive", message.Content.Headers.ContentType.ToString());
          Assert.Equal(11541, bytes.Length);
          Assert.Equal("eca06bb19a4f55673f8f40d0a20eb0ee0342403ee5856b890d6c612e5facb027", SHA256(bytes));
        });

      Assert.Equal(11541, new FileInfo(
        Path.Combine(myTempDirectory, "repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/cache-ant-xz-1.10.5.jar")).Length);
    }

    [Fact]
    public async void Head_With_Existing_File()
    {
      await AssertHeadResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
        message => AssertStatusHeader(message, CachingProxyStatus.MISS));
      await AssertHeadResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
        message => AssertStatusHeader(message, CachingProxyStatus.HIT));
    }

    [Fact]
    public async void Head_With_Missing_File()
    {
      await AssertHeadResponse("/repo1.maven.org/maven2/notfound.txt", HttpStatusCode.NotFound,
        message => AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS));
      await AssertHeadResponse("/repo1.maven.org/maven2/notfound.txt", HttpStatusCode.NotFound,
        message => AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT));
    }

    [Fact]
    public async void Caching_Works_Unknown_ContentLength()
    {
      var url = "/plugins.gradle.org/m2/de/undercouch/gradle-download-task/3.4.2/gradle-download-task-3.4.2.pom.sha1";
      await AssertGetResponse(url, HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.MISS);
          Assert.Null(GetContentLength(message));
          Assert.Equal("49a1b31825c921fd25dd374f314245060eb6cae0", Encoding.UTF8.GetString(bytes));
        });

      await AssertGetResponse(url, HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertNoStatusHeader(message);
          Assert.Equal(40, GetContentLength(message));
          Assert.Equal("49a1b31825c921fd25dd374f314245060eb6cae0", Encoding.UTF8.GetString(bytes));
        });
    }

    [Fact]
    public async void Parallel_Requests()
    {
      var url = "/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar";

      var response1 = myServer.CreateClient().GetAsync(url);
      var response2 = myServer.CreateClient().GetAsync(url);

      var result = await Task.WhenAll(response1, response2);

      AssertStatusHeader(result[0], CachingProxyStatus.MISS);
      AssertStatusHeader(result[1], CachingProxyStatus.MISS);

      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
        (message, bytes) => { AssertNoStatusHeader(message); });
    }

    [Fact]
    public async void Always_Redirect_Snapshots()
    {
      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.0-SNAPSHOT/ant-xz-1.0-SNAPSHOT.jar",
        HttpStatusCode.TemporaryRedirect,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
          Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz/1.0-SNAPSHOT/ant-xz-1.0-SNAPSHOT.jar",
            message.Headers.Location.ToString());
        });
    }

    [Fact]
    public async void Always_Redirect_Directory_Index()
    {
      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/",
        HttpStatusCode.TemporaryRedirect,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
          Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz/",
            message.Headers.Location.ToString());
        });
    }

    [Fact]
    public async void Always_Redirect_Directory_Index_No_Trailing_Slash()
    {
      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz",
        HttpStatusCode.TemporaryRedirect,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
          Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz",
            message.Headers.Location.ToString());
        });
    }

    [Fact]
    public async void Always_Blacklist_MavenMetadataXml()
    {
      await AssertGetResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/maven-metadata.xml", HttpStatusCode.NotFound,
        (message, bytes) => { AssertStatusHeader(message, CachingProxyStatus.BLACKLISTED); });
    }

    [Fact]
    public async void No_Route_To_Host()
    {
      // https://en.wikipedia.org/wiki/Reserved_IP_addresses
      // 198.51.100.0/24 reserved for documentation
      await AssertGetResponse("/198.51.100.9/a.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
          AssertCachedStatusHeader(message, HttpStatusCode.GatewayTimeout);
        });

      await AssertGetResponse("/198.51.100.9/a.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
          AssertCachedStatusHeader(message, HttpStatusCode.GatewayTimeout);
        });
    }

    [Fact]
    public async void Unknown_Host()
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
    public async void Remote_NotFound()
    {
      await AssertGetResponse("/repo1.maven.org/maven2/not_found.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
          AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        });

      await AssertGetResponse("/repo1.maven.org/maven2/not_found.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
          AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async void Unknown_Prefix()
    {
      await AssertGetResponse("/some_unknown_prefix/a.txt", HttpStatusCode.NotFound,
        (message, bytes) => { AssertNoStatusHeader(message); });
    }

    private async Task AssertGetResponse(string url, HttpStatusCode expectedCode,
      Action<HttpResponseMessage, byte[]> assertions)
    {
      myOutput.WriteLine("*** GET " + url);
      using (var response = await myServer.CreateClient().GetAsync(url))
      {
        var bytes = await response.Content.ReadAsByteArrayAsync();

        myOutput.WriteLine(response.ToString());
        if (bytes.All(c => c < 128) && bytes.Length < 200)
          myOutput.WriteLine("Body: " + Encoding.UTF8.GetString(bytes));

        Assert.Equal(expectedCode, response.StatusCode);
        assertions(response, bytes);
      }
    }

    private async Task AssertHeadResponse(string url, HttpStatusCode expectedCode,
      Action<HttpResponseMessage> assertions)
    {
      myOutput.WriteLine("*** HEAD " + url);
      using (var response = await myServer.CreateClient().SendAsync(
        new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseContentRead))
      {
        myOutput.WriteLine(response.ToString());
        Assert.Equal(expectedCode, response.StatusCode);
        assertions(response);
      }
    }

    private long? GetContentLength(HttpResponseMessage response)
    {
      var values = response.Content.Headers.FirstOrDefault(x => x.Key == "Content-Length").Value;
      if (values == null) return null;
      return long.Parse(values.FirstOrDefault());
    }

    private void AssertStatusHeader(HttpResponseMessage response, CachingProxyStatus status)
    {
      var statusHeader = response.Headers.GetValues(CachingProxyConstants.StatusHeader).FirstOrDefault();
      Assert.Equal(statusHeader, status.ToString());
    }

    private void AssertCachedStatusHeader(HttpResponseMessage response, HttpStatusCode status)
    {
      var statusHeader = response.Headers.GetValues(CachingProxyConstants.CachedStatusHeader).FirstOrDefault();
      Assert.Equal(statusHeader, ((int) status).ToString());
    }

    private void AssertNoStatusHeader(HttpResponseMessage response)
    {
      if (response.Headers.TryGetValues(CachingProxyConstants.StatusHeader, out var headers))
      {
        throw new Exception($"Expected no {CachingProxyConstants.StatusHeader} header, but got: " + string.Join(", ", headers));
      }
    }

    private static string SHA256(byte[] input)
    {
      var hash = (new SHA256Managed()).ComputeHash(input);
      return string.Join("", hash.Select(b => b.ToString("x2")).ToArray());
    }

    public void Dispose()
    {
      myServer?.Dispose();
      Directory.Delete(myTempDirectory, true);
    }
  }
}
