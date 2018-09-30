using System;
using System.Collections.Generic;
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
using Microsoft.EntityFrameworkCore.Internal;
using Xunit;
using Xunit.Abstractions;

namespace JetBrains.CachingProxy.Tests
{
  // TODO: Test with wrong content-length from remote side
  // TODO: Test remote 5xx
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

      var config = new CachingProxyConfig()
      {
        LocalCachePath = myTempDirectory,
        Prefixes = new List<string>
        {
          "/repo1.maven.org/maven2",
          "/198.51.100.9",
          "/unknown_host.xyz"
        }
      };

      var builder = new WebHostBuilder()
        .Configure(app => { app.UseMiddleware<CachingProxy>(config); }
        );

      myServer = new TestServer(builder);
    }

    [Fact]
    public async void Caching_Works()
    {
      await AssertResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.MISS);
          Assert.Equal("application/java-archive", message.Content.Headers.ContentType.ToString());
          Assert.Equal(11541, message.Content.Headers.ContentLength);
          Assert.Equal(11541, bytes.Length);
          Assert.Equal("eca06bb19a4f55673f8f40d0a20eb0ee0342403ee5856b890d6c612e5facb027", SHA256(bytes));
        });

      await AssertResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertNoStatusHeader(message);
          Assert.Equal(11541, message.Content.Headers.ContentLength);
          Assert.Equal("application/java-archive", message.Content.Headers.ContentType.ToString());
          Assert.Equal(11541, bytes.Length);
          Assert.Equal("eca06bb19a4f55673f8f40d0a20eb0ee0342403ee5856b890d6c612e5facb027", SHA256(bytes));
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

      await AssertResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar", HttpStatusCode.OK,
        (message, bytes) =>
        {
          AssertNoStatusHeader(message);
        });
    }

    [Fact]
    public async void Always_Redirect_Snapshots()
    {
      await AssertResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.0-SNAPSHOT/ant-xz-1.0-SNAPSHOT.jar", HttpStatusCode.TemporaryRedirect,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.ALWAYS_REDIRECT);
          Assert.Equal("https://repo1.maven.org/maven2/org/apache/ant/ant-xz/1.0-SNAPSHOT/ant-xz-1.0-SNAPSHOT.jar", message.Headers.Location.ToString());
        });
    }

    [Fact]
    public async void Always_Blacklist_MavenMetadataXml()
    {
      await AssertResponse("/repo1.maven.org/maven2/org/apache/ant/ant-xz/maven-metadata.xml", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.BLACKLISTED);
        });
    }

    [Fact]
    public async void No_Route_To_Host()
    {
      // https://en.wikipedia.org/wiki/Reserved_IP_addresses
      // 198.51.100.0/24 reserved for documentation
      await AssertResponse("/198.51.100.9/a.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
          AssertCachedStatusHeader(message, HttpStatusCode.GatewayTimeout);
        });

      await AssertResponse("/198.51.100.9/a.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
          AssertCachedStatusHeader(message, HttpStatusCode.GatewayTimeout);
        });
    }

    [Fact]
    public async void Unknown_Host()
    {
      await AssertResponse("/unknown_host.xyz/a.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
          AssertCachedStatusHeader(message, HttpStatusCode.ServiceUnavailable);
        });

      await AssertResponse("/unknown_host.xyz/a.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
          AssertCachedStatusHeader(message, HttpStatusCode.ServiceUnavailable);
        });
    }

    [Fact]
    public async void Remote_NotFound()
    {
      await AssertResponse("/repo1.maven.org/maven2/not_found.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_MISS);
          AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        });

      await AssertResponse("/repo1.maven.org/maven2/not_found.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertStatusHeader(message, CachingProxyStatus.NEGATIVE_HIT);
          AssertCachedStatusHeader(message, HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async void Unknown_Prefix()
    {
      await AssertResponse("/some_unknown_prefix/a.txt", HttpStatusCode.NotFound,
        (message, bytes) =>
        {
          AssertNoStatusHeader(message);
        });
    }

    private async Task AssertResponse(string url, HttpStatusCode expectedCode,
      Action<HttpResponseMessage, byte[]> assertions)
    {
      myOutput.WriteLine("*** Query " + url);
      var response = await myServer.CreateClient().GetAsync(url);
      var bytes = await response.Content.ReadAsByteArrayAsync();

      myOutput.WriteLine(response.ToString());
      if (bytes.All(c => c < 128) && bytes.Length < 200)
        myOutput.WriteLine("Body: " + Encoding.UTF8.GetString(bytes));

      Assert.Equal(expectedCode, response.StatusCode);
      assertions(response, bytes);
    }

    private void AssertStatusHeader(HttpResponseMessage response, CachingProxyStatus status)
    {
      var statusHeader = response.Headers.GetValues(CachingProxyConstants.StatusHeader).FirstOrDefault();
      Assert.Equal(statusHeader, status.ToString());
    }

    private void AssertCachedStatusHeader(HttpResponseMessage response, HttpStatusCode status)
    {
      var statusHeader = response.Headers.GetValues(CachingProxyConstants.CachedStatusHeader).FirstOrDefault();
      Assert.Equal(statusHeader, ((int)status).ToString());
    }

    private void AssertNoStatusHeader(HttpResponseMessage response)
    {
      if (response.Headers.TryGetValues(CachingProxyConstants.StatusHeader, out var headers))
      {
        throw new Exception($"Expected no {CachingProxyConstants.StatusHeader} header, but got: " +
                            headers.Join(","));
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