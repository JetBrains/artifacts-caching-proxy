using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// Exercises the S3-backed configuration (Program picks S3CachingMiddleware when S3.BucketName is set).
// A FakeAmazonS3 stands in for the AWS client so no network/credentials are required.
[SuppressMessage("ReSharper", "UnusedParameter.Local")]
public class S3CachingMiddlewareTest(UpstreamTestServer upstreamServer)
  : IAsyncLifetime, IClassFixture<UpstreamTestServer>
{
  private const string BucketName = "test-bucket";

  private readonly FakeAmazonS3 myS3 = new();
  private readonly List<IHost> myHosts = [];

  private TestServer CreateServer(bool signedLinks)
  {
    var config = new CachingProxyConfig
    {
      S3 = new CachingProxyConfig.S3Config(BucketName, signedLinks),
      Prefixes = [$"/real={upstreamServer.Url}"],
    };

    var host = new HostBuilder()
      .ConfigureWebHost(webHostBuilder =>
      {
        // ConfigureOurApp reads context.Configuration to choose the S3 branch, so the S3 settings
        // must be present in the host configuration (not only in the locally-built one below).
        webHostBuilder
          .UseTestServer()
          .ConfigureAppConfiguration(cfg => cfg.AddJsonStream(ToJsonStream(config)))
          .ConfigureTestServices(services =>
          {
            var configuration = new ConfigurationBuilder().AddJsonStream(ToJsonStream(config)).Build();
            services.AddSingleton(config);
            Program.ConfigureOurServices(services, configuration);
            // Real system clock: the signed-link path lets the real client compute the presigned
            // URL, whose Expires must be in the future (no test here advances time).
            services.Replace(ServiceDescriptor.Singleton<IAmazonS3>(myS3));
          })
          .Configure((context, builder) => Program.ConfigureOurApp(builder, context.Configuration));
      })
      .Build();

    myHosts.Add(host);
    host.Start();
    return host.GetTestServer();
  }

  private static MemoryStream ToJsonStream(CachingProxyConfig config) =>
    new(JsonSerializer.SerializeToUtf8Bytes(config));

  [Fact]
  public async Task Health_Reports_Bucket_Acl()
  {
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/health").GetAsync();
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
  }

  [Fact]
  public async Task Get_Miss_Uploads_To_S3_And_Redirects()
  {
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);
    // Redirect points at a presigned URL for the object key.
    var location = response.Headers.Location?.ToString();
    Assert.NotNull(location);
    Assert.Contains("real/a.jar", location);
    Assert.Contains("X-Amz-", location); // presigned query parameters

    // The upstream body was streamed into the bucket, not back to the client.
    Assert.Equal(1, myS3.PutObjectCalls);
    Assert.True(myS3.Objects.TryGetValue("real/a.jar", out var stored));
    Assert.Equal("a.jar", Encoding.UTF8.GetString(stored.Body));
  }

  [Fact]
  public async Task Get_Without_Content_Length_Is_Spooled_And_Uploaded()
  {
    // Upstream responds chunked (no Content-Length). The body must still land in the bucket intact,
    // spooled via a temp file rather than buffered whole in memory.
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real/chunked.bin").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.True(myS3.Objects.TryGetValue("real/chunked.bin", out var stored));
    Assert.Equal("chunk1chunk2", Encoding.UTF8.GetString(stored.Body));
  }

  [Fact]
  public async Task Head_Miss_Returns_Upstream_Metadata_Without_Upload()
  {
    // A HEAD has no body to store, so on a miss it is answered with the upstream metadata
    // (MISS, 200) rather than uploaded/redirected. The positive result is cached in memory,
    // so a second HEAD is a HIT.
    var server = CreateServer(signedLinks: true);

    using (var first = await server.CreateRequest("/real/extensionless").SendAsync(HttpMethod.Head.Method))
    {
      Assert.Equal(HttpStatusCode.OK, first.StatusCode);
      AssertStatusHeader(first, CachingProxyStatus.MISS);
    }

    using var second = await server.CreateRequest("/real/extensionless").SendAsync(HttpMethod.Head.Method);
    Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    AssertStatusHeader(second, CachingProxyStatus.HIT);

    Assert.Equal(0, myS3.PutObjectCalls);
  }

  [Fact]
  public async Task Existing_Object_Redirects_Without_Reupload()
  {
    myS3.Objects["real/a.jar"] = (Encoding.UTF8.GetBytes("a.jar"), "application/java-archive");

    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.Equal(1, myS3.GetObjectMetadataCalls); // probed S3
    Assert.Equal(0, myS3.PutObjectCalls);         // but did not re-upload
  }

  [Fact]
  public async Task Second_Request_Is_Served_From_Memory_Cache()
  {
    var server = CreateServer(signedLinks: true);

    using (var first = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method))
      AssertStatusHeader(first, CachingProxyStatus.MISS);

    using var second = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    Assert.Equal(HttpStatusCode.RedirectKeepVerb, second.StatusCode);
    AssertStatusHeader(second, CachingProxyStatus.HIT);
    Assert.Equal(1, myS3.PutObjectCalls); // not uploaded again
  }

  [Fact]
  public async Task Get_After_Head_Regenerates_Verb_Correct_Redirect()
  {
    // Regression: a presigned URL is signed for a specific verb. A HEAD caches a HEAD-signed
    // redirect; a following GET must regenerate a GET-signed redirect rather than replaying the
    // cached HEAD-signed one (which S3 would reject as SignatureDoesNotMatch).
    myS3.Objects["real/a.jar"] = (Encoding.UTF8.GetBytes("a.jar"), null);

    var server = CreateServer(signedLinks: true);

    using (var head = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Head.Method))
      Assert.Equal(HttpStatusCode.RedirectKeepVerb, head.StatusCode);
    Assert.Equal(HttpVerb.HEAD, myS3.LastPresignVerb);

    using var get = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    Assert.Equal(HttpStatusCode.RedirectKeepVerb, get.StatusCode);
    AssertStatusHeader(get, CachingProxyStatus.HIT);
    Assert.Equal(HttpVerb.GET, myS3.LastPresignVerb);
  }

  [Fact]
  public async Task Unsigned_Links_Redirect_To_Bucket_Endpoint()
  {
    var server = CreateServer(signedLinks: false);
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    var location = response.Headers.Location?.ToString();
    Assert.NotNull(location);
    // Exactly one separator between the bucket endpoint and the key (regression for the URL join).
    Assert.EndsWith("/real/a.jar", location);
    Assert.DoesNotContain("//real/a.jar", location);
  }

  [Fact]
  public async Task Post_Is_Rejected_Before_Probing_S3()
  {
    // Regression test: even when the object exists in the bucket, a non-GET/HEAD method must be
    // rejected up front and must NOT be redirected (the validation-bypass fix).
    myS3.Objects["real/a.jar"] = (Encoding.UTF8.GetBytes("a.jar"), null);

    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Post.Method);

    Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    Assert.Equal(0, myS3.GetObjectMetadataCalls);
  }

  [Fact]
  public async Task Invalid_Path_Is_Rejected_Before_Probing_S3()
  {
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real/bad~name.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.BAD_REQUEST);
    Assert.Equal(0, myS3.GetObjectMetadataCalls);
  }

  [Fact]
  public async Task Upstream_NotFound_Is_Negatively_Cached_Not_Uploaded()
  {
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real/does-not-exist.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.NEGATIVE_MISS);
    Assert.Equal(0, myS3.PutObjectCalls);
  }

  private static void AssertStatusHeader(HttpResponseMessage response, CachingProxyStatus status) =>
    Assert.Equal(status.ToString(), response.Headers.GetValues(CachingProxyConstants.StatusHeader).First());

  Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

  async Task IAsyncLifetime.DisposeAsync()
  {
    foreach (var host in myHosts)
      await host.StopAsync();
    myS3.Dispose();
  }

  /// <summary>
  /// Minimal in-memory <see cref="IAmazonS3"/> stand-in. Subclasses the real client (so the huge
  /// interface surface is inherited) and overrides only the operations the middleware invokes.
  /// </summary>
  private sealed class FakeAmazonS3() : AmazonS3Client(
    new BasicAWSCredentials("test", "test"), new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1 })
  {
    public readonly Dictionary<string, (byte[] Body, string? ContentType)> Objects = new();
    public int GetObjectMetadataCalls;
    public int PutObjectCalls;
    public HttpVerb? LastPresignVerb;

    public override Task<string> GetPreSignedURLAsync(GetPreSignedUrlRequest request)
    {
      LastPresignVerb = request.Verb;
      return base.GetPreSignedURLAsync(request);
    }

    public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref GetObjectMetadataCalls);
      if (Objects.ContainsKey(key))
        return Task.FromResult(new GetObjectMetadataResponse { HttpStatusCode = HttpStatusCode.OK });
      throw new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound };
    }

    public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref PutObjectCalls);
      using var ms = new MemoryStream();
      await request.InputStream.CopyToAsync(ms, cancellationToken);
      Objects[request.Key] = (ms.ToArray(), request.Headers.ContentType);
      return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK };
    }

    public override Task<GetBucketAclResponse> GetBucketAclAsync(GetBucketAclRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new GetBucketAclResponse { HttpStatusCode = HttpStatusCode.OK });
  }
}
