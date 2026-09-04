using System;
using System.Collections.Generic;
using System.Globalization;
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
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.CysharpMemoryPack;

namespace JetBrains.CachingProxy.Tests;

// Exercises the S3-backed configuration (Program picks S3CachingMiddleware when S3.BucketName is set).
// A FakeAmazonS3 stands in for the AWS client so no network/credentials are required.
public class S3CachingMiddlewareTest(UpstreamTestServer upstreamServer)
  : IAsyncLifetime, IClassFixture<UpstreamTestServer>
{
  private const string BucketName = "test-bucket";

  // The prefetch window these tests pin via CreateServer, independent of the production default
  // (S3Config.InlineThresholdBytes) so raising that default never silently flips the inline/redirect
  // expectations below. Objects up to this size inline; larger ones redirect.
  private const int TestInlineThresholdBytes = 16 * 1024;

  // 32 KiB: larger than the pinned test window (so it redirects in most tests), and exactly a raised
  // 32 KiB window (so Inline_Threshold_Is_Configurable inlines it).
  private static readonly byte[] ourLargeBody = new byte[32 * 1024];

  // The production defaults, for the tests that assert them and as the fallback for the ones that do not
  // care (CreateServer).
  private static readonly CachingProxyConfig.S3Config DefaultS3Config = new(BucketName);

  // S3's own single-PutObject ceiling, which is what MultipartThresholdBytes has to default to.
  private const long SinglePutLimitBytes = 5L * 1024 * 1024 * 1024;

  private readonly FakeAmazonS3 myS3 = new();
  private readonly List<IHost> myHosts = [];
  private readonly RemoteServers.RemoteServer myRemoteServer = new("/real", upstreamServer.Url, new CacheDuration());

  private TestServer CreateServer(bool signedLinks, TimeSpan? signedLinkTTL = null, int inlineThresholdBytes = TestInlineThresholdBytes,
    CacheDuration? distributedCacheDuration = null, TimeSpan? refreshAfter = null, CacheDuration? prefixCacheDuration = null,
    bool varyByAccept = false, long? multipartThresholdBytes = null, int? multipartPartSizeBytes = null)
  {
    // When a freshness window is requested, drive it through a profile with a catch-all rule (so every
    // /real path is revalidated after the window) — mirroring how the disk tests exercise revalidation.
    var profiles = new Dictionary<string, CachingProfile>();
    CachingProxyPrefix prefix = new($"/real={upstreamServer.Url}", prefixCacheDuration);
    if (refreshAfter != null || varyByAccept)
    {
      profiles["fresh"] = new CachingProfile
      {
        Rules = [new CachingRule { Pattern = ".", RefreshAfter = refreshAfter, VaryByAccept = varyByAccept }]
      };
      prefix = new CachingProxyPrefix($"/real={upstreamServer.Url}", prefixCacheDuration, Profile: "fresh");
    }

    var config = new CachingProxyConfig
    {
      S3 = new CachingProxyConfig.S3Config(BucketName, signedLinks)
      {
        SignedLinkTTL = signedLinkTTL ?? TimeSpan.FromMinutes(10),
        InlineThresholdBytes = inlineThresholdBytes,
        // Left at the production 5 GiB unless a test asks otherwise: the multipart path is only
        // reachable in a test by lowering the threshold to the size of a test body.
        MultipartThresholdBytes = multipartThresholdBytes ?? DefaultS3Config.MultipartThresholdBytes,
        MultipartPartSizeBytes = multipartPartSizeBytes ?? DefaultS3Config.MultipartPartSizeBytes,
      },
      DistributedCacheDuration = distributedCacheDuration ?? new CacheDuration(),
      CachingProfiles = profiles,
      Prefixes = [prefix],
    };

    var host = new HostBuilder()
      .ConfigureWebHost(webHostBuilder =>
      {
        // ConfigureOurApp reads context.Configuration to choose the S3 branch, so the S3 settings
        // must be present in the host configuration (not only in the locally-built one below).
        webHostBuilder
          .UseTestServer()
          .ConfigureAppConfiguration(cfg =>
            cfg.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(config))))
          .ConfigureOurServices()
          .ConfigureServices(services =>
          {
            services
              .AddSingleton(config)
            // Real system clock: the signed-link path lets the real client compute the presigned
            // URL, whose Expires must be in the future (no test here advances time).
              .Replace(ServiceDescriptor.Singleton<IAmazonS3>(myS3));

            // Opt-in L2: wire an in-memory distributed cache so HasDistributedCache is true and the
            // DistributedCacheDuration TTLs actually apply, without needing a real Redis (Program only
            // wires L2 when a Redis connection string is configured).
            if (distributedCacheDuration != null)
            {
              CachedResponseFormatter.Register();
              services.AddSingleton<IDistributedCache>(
                new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
              services.AddFusionCache()
                .WithSerializer(new FusionCacheCysharpMemoryPackSerializer())
                // The registered L2 here is a MemoryDistributedCache, which the builder treats as
                // "not a real distributed cache" and skips by default; opt in so HasDistributedCache holds.
                .WithRegisteredDistributedCache(ignoreMemoryDistributedCache: false);
            }
          })
          .Configure((context, builder) => builder.ConfigureOurApp(context.Configuration));
      })
      .Build();

    myHosts.Add(host);
    host.Start();
    return host.GetTestServer();
  }

  [Fact]
  public async Task Health_Reports_Bucket_Acl()
  {
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/health").GetAsync();
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("OK", await response.Content.ReadAsStringAsync());
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
    Assert.Contains(GetPathKey("/real/a.jar"), location); // hashed object key
    Assert.Contains("X-Amz-", location); // presigned query parameters

    // The upstream body was streamed into the bucket, not back to the client.
    Assert.Equal(1, myS3.PutObjectCalls);
    Assert.True(myS3.Objects.TryGetValue(GetPathKey("/real/a.jar"), out var stored));
    Assert.Equal("a.jar", Encoding.UTF8.GetString(stored.Body));
  }

  [Fact]
  public void Multipart_Threshold_Defaults_To_S3s_Single_Put_Ceiling()
  {
    // The bug this whole path exists for (MRI-5371): a 5.68 GiB engine archive went out as one PUT,
    // S3 answered EntityTooLarge, and the client got a 503 for an artifact the upstream had served.
    // A default above S3's ceiling puts that back, and no other test would notice.
    Assert.Equal(SinglePutLimitBytes, DefaultS3Config.MultipartThresholdBytes);
  }

  [Theory]
  // Below the part budget the floor decides, so the buffer stays at one part's worth.
  [InlineData(1, 16 * 1024 * 1024)]
  [InlineData(6L * 1024 * 1024 * 1024, 16 * 1024 * 1024)]
  // 10,000 * 16 MiB is the last size the floor covers; one byte past it the part grows just enough.
  [InlineData(10_000L * 16 * 1024 * 1024, 16 * 1024 * 1024)]
  [InlineData(10_000L * 16 * 1024 * 1024 + 1, 16 * 1024 * 1024 + 1)]
  // S3's own 5 TiB object ceiling, the largest part this can ever be asked for.
  [InlineData(5L * 1024 * 1024 * 1024 * 1024, 549755814)]
  public void Part_Size_Is_Floored_And_Grows_To_Fit_The_Part_Budget(long contentLength, int expectedPartSize)
  {
    var partSize = S3CachingMiddleware.PartSizeFor(contentLength, DefaultS3Config.MultipartPartSizeBytes);

    Assert.Equal(expectedPartSize, partSize);
    // The budget is the hard constraint: a part size that needs more than 10,000 parts cannot complete.
    Assert.True((contentLength + partSize - 1) / partSize <= 10_000);
  }

  [Fact]
  public async Task Get_Miss_Uploads_A_Large_Object_In_Parts()
  {
    // "sized.jar" is 9 bytes and declares a Content-Length, so it streams straight through without the
    // temp-file spool: 4-byte parts split it 4 + 4 + 1, exercising a short final part.
    var server = CreateServer(signedLinks: true, multipartThresholdBytes: 9, multipartPartSizeBytes: 4);
    using var response = await server.CreateRequest("/real/sized.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);

    Assert.Equal(0, myS3.PutObjectCalls); // not a single PUT - that is the point
    Assert.Equal(3, myS3.UploadPartCalls);
    Assert.Equal(0, myS3.AbortMultipartUploadCalls);
    Assert.Equal([4, 4, 1], myS3.CompletedPartSizes);

    // The assembled object is byte-identical to the upstream body, and carries the same metadata a
    // single PUT would have stored.
    var key = GetPathKey("/real/sized.jar");
    Assert.True(myS3.Objects.TryGetValue(key, out var stored));
    Assert.Equal("sized.jar", Encoding.UTF8.GetString(stored.Body));
    Assert.Equal(upstreamServer.Url + "sized.jar", myS3.PutObjectUris[key]);
    Assert.True(myS3.CreatedAt.ContainsKey(key));
  }

  [Fact]
  public async Task Get_Miss_Uploads_A_Large_Chunked_Object_In_Parts()
  {
    // No upstream Content-Length (chunked), so the body is spooled to a temp file first and the part
    // count comes from the spooled length: "chunk1chunk2" is 12 bytes, split 5 + 5 + 2.
    var server = CreateServer(signedLinks: true, multipartThresholdBytes: 12, multipartPartSizeBytes: 5);
    using var response = await server.CreateRequest("/real/chunked.bin").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);

    Assert.Equal(0, myS3.PutObjectCalls);
    Assert.Equal([5, 5, 2], myS3.CompletedPartSizes);
    Assert.True(myS3.Objects.TryGetValue(GetPathKey("/real/chunked.bin"), out var stored));
    Assert.Equal("chunk1chunk2", Encoding.UTF8.GetString(stored.Body));
  }

  [Fact]
  public async Task Failed_Part_Upload_Aborts_The_Multipart_Upload()
  {
    // An upload left open keeps billing for the parts already stored, and an object listing does not
    // show it, so the failure path has to abandon it explicitly.
    myS3.FailUploadPartNumber = 2;
    var server = CreateServer(signedLinks: true, multipartThresholdBytes: 9, multipartPartSizeBytes: 4);

    using var response = await server.CreateRequest("/real/sized.jar").SendAsync(HttpMethod.Get.Method);

    // The artifact exists upstream and only the cache write failed, so this is a 503 the client retries
    // rather than a negatively-cached 404 (see S3CachingMiddleware.InvokeAsync).
    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    Assert.Equal(1, myS3.AbortMultipartUploadCalls);
    Assert.DoesNotContain(GetPathKey("/real/sized.jar"), myS3.Objects.Keys);
  }

  [Fact]
  public async Task Miss_Redirect_Is_Not_Cached_Past_The_Freshness_Window()
  {
    // The production shape this guards: the regional deployments cache a 307 for an hour, while an OCI
    // manifest by tag is fresh for five minutes. The cached redirect is replayed without probing the
    // bucket, and the probe is where the window is enforced, so a redirect outliving the window is a
    // manifest served stale for the difference — an hour instead of five minutes.
    var window = TimeSpan.FromMinutes(5);
    var server = CreateServer(signedLinks: true, refreshAfter: window,
      prefixCacheDuration: new CacheDuration { [HttpStatusCode.RedirectKeepVerb] = TimeSpan.FromHours(1) });

    var before = DateTimeOffset.UtcNow;
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    var after = DateTimeOffset.UtcNow;

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);

    var until = DateTimeOffset.ParseExact(
      response.Headers.GetValues(CachingProxyConstants.CachedUntilHeader).Single(), "R", CultureInfo.InvariantCulture);
    // "R" truncates to the second, so the reported instant can sit just under the window's start.
    Assert.InRange(until, before + window - TimeSpan.FromSeconds(1), after + window);
  }

  [Fact]
  public async Task Content_Metric_Counts_The_Bytes_A_Redirect_Sends_A_Client_To_Fetch()
  {
    // In bucket mode the artifact is delivered by the redirect: the client downloads the object from S3,
    // and that is the traffic this request produced, whatever status sent it there. The bytes cannot be
    // read off the response (a 307 has no body of its own), so they travel on the redirect's internal
    // Cached-Content-Length header, and the HIT that replays the stored redirect reports the same size
    // again - the client downloads it again too.
    var server = CreateServer(signedLinks: false);
    using var metrics = new RequestMetricRecorder(server.Services);

    using (var miss = await server.CreateRequest("/real/sized.jar").SendAsync(HttpMethod.Get.Method))
    {
      Assert.Equal(HttpStatusCode.RedirectKeepVerb, miss.StatusCode);
      AssertStatusHeader(miss, CachingProxyStatus.MISS);
      // Internal bookkeeping, and a wrong Content-Length at that (a 307 sends no body): it must stay
      // inside the process and never reach the client.
      Assert.DoesNotContain(CachingProxyConstants.CachedContentLengthHeader,
        miss.Headers.Select(static h => h.Key), StringComparer.OrdinalIgnoreCase);
    }

    using (var hit = await server.CreateRequest("/real/sized.jar").SendAsync(HttpMethod.Get.Method))
    {
      Assert.Equal(HttpStatusCode.RedirectKeepVerb, hit.StatusCode);
      AssertStatusHeader(hit, CachingProxyStatus.HIT);
    }

    Assert.Equal(1, myS3.PutObjectCalls);
    Assert.Equal("sized.jar".Length, myS3.Objects[GetPathKey("/real/sized.jar")].Body.Length);

    // One MISS for one upload, not two. The head RemoteProxy.ProcessAsync would write for the body is
    // suppressed here (this middleware Clear()s it and redirects instead), so the redirect is the only
    // report the request makes - otherwise both requests and bytes come out doubled for every object the
    // bucket gains.
    Assert.Equal(["MISS", "HIT"], metrics.TagValues("status"));
    Assert.Equal([("MISS", 9L), ("HIT", 9L)], metrics.ContentBytes);
  }

  [Fact]
  public async Task Content_Metric_Counts_A_Redirected_Objects_Full_Size()
  {
    // The probe path: the object is already in the bucket, and the ranged prefetch read only its first
    // slice before deciding it is too large to inline. The bytes counted are the object's own size, not
    // the size of that slice - anything else scales with the inline window rather than with the traffic.
    var server = CreateServer(signedLinks: false);
    myS3.Objects[GetPathKey("/real/a.jar")] = (ourLargeBody, "application/java-archive", null);
    using var metrics = new RequestMetricRecorder(server.Services);

    using (var miss = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method))
    {
      Assert.Equal(HttpStatusCode.RedirectKeepVerb, miss.StatusCode);
      AssertStatusHeader(miss, CachingProxyStatus.MISS);
    }

    Assert.Equal(0, myS3.PutObjectCalls); // already in the bucket: nothing was fetched or uploaded
    Assert.Equal([("MISS", (long)ourLargeBody.Length)], metrics.ContentBytes);
  }

  [Fact]
  public async Task Content_Metric_Counts_An_Inlined_Objects_Bytes()
  {
    // The other half of the probe path: small enough to fit the prefetch window, so it is served from
    // memory with its body rather than redirected, and its length is on the response itself. Same status
    // and same tags as the redirect above - which is the point, one byte counter covering both shapes.
    var server = CreateServer(signedLinks: false);
    myS3.Objects[GetPathKey("/real/a.jar")] = ([.. "a.jar"u8], "application/java-archive", null);
    using var metrics = new RequestMetricRecorder(server.Services);

    using (var miss = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method))
    {
      Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
      AssertStatusHeader(miss, CachingProxyStatus.MISS);
      Assert.Equal("a.jar", await miss.Content.ReadAsStringAsync());
    }

    Assert.Equal([("MISS", 5L)], metrics.ContentBytes);
  }

  [Fact]
  public async Task Content_Metric_Counts_No_Bytes_For_A_Head()
  {
    // A HEAD of an object too large to inline is answered from memory with the object's full size in its
    // Content-Length - and sends none of it, nor a redirect to fetch it. So the request counts and the
    // bytes do not, however big the object; the GET that follows is what delivers it.
    var server = CreateServer(signedLinks: false);
    myS3.Objects[GetPathKey("/real/a.jar")] = (ourLargeBody, "application/java-archive", null);
    using var metrics = new RequestMetricRecorder(server.Services);

    using (var head = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Head.Method))
    {
      Assert.Equal(HttpStatusCode.OK, head.StatusCode);
      AssertStatusHeader(head, CachingProxyStatus.MISS);
      Assert.Equal(ourLargeBody.Length, head.Content.Headers.ContentLength);
    }

    using (var get = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method))
    {
      Assert.Equal(HttpStatusCode.RedirectKeepVerb, get.StatusCode);
      AssertStatusHeader(get, CachingProxyStatus.MISS);
    }

    Assert.Equal(["MISS", "MISS"], metrics.TagValues("status"));
    Assert.Equal([("MISS", (long)ourLargeBody.Length)], metrics.ContentBytes);
  }

  [Fact]
  public async Task Content_Metric_Counts_Nothing_For_A_Heads_Re_Upload()
  {
    // The heaviest request that counts nothing: this HEAD's revalidation found the object changed, so it
    // pulled the whole entity from the upstream and re-uploaded it to the bucket. The client is handed a
    // head and no body, and is not redirected either, so nothing was served to it - however much traffic
    // the request moved behind the scenes.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"v1\"");
    myS3.CreatedAt[key] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    upstreamServer.RevalidateContent = "v2-longer";
    using var metrics = new RequestMetricRecorder(server.Services);

    using (var head = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Head.Method))
    {
      Assert.Equal(HttpStatusCode.OK, head.StatusCode);
      AssertStatusHeader(head, CachingProxyStatus.REVALIDATED);
      Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    Assert.Equal(1, myS3.PutObjectCalls);
    Assert.Equal("v2-longer", Encoding.UTF8.GetString(myS3.Objects[key].Body));
    Assert.Equal(["REVALIDATED"], metrics.TagValues("status"));
    Assert.Empty(metrics.ContentBytes);
  }

  [Fact]
  public async Task Get_Miss_Stores_Upstream_Uri_In_Object_Metadata()
  {
    // The S3 key is an opaque hash, so the original upstream URI is preserved as the object's
    // "uri" user-metadata for traceability (e.g. reverse-mapping a hashed key back to its source).
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    Assert.Equal(1, myS3.PutObjectCalls);

    var key = GetPathKey("/real/a.jar");
    Assert.True(myS3.PutObjectUris.TryGetValue(key, out var storedUri));
    Assert.Equal(myRemoteServer.GetUpstreamUri("a.jar")!.ToString(), storedUri);
  }

  [Fact]
  public async Task Oci_Digest_Travels_With_The_Object()
  {
    // Docker-Content-Digest covers the exact bytes of one representation, so it cannot be recomputed from
    // the stored copy - it has to travel with the object. An OCI client resolves a manifest by it and
    // verifies the body against it, so a hit that dropped it would break a pull that a miss served fine.
    // Nothing profile-specific about it: any object whose upstream sent one keeps it.
    var server = CreateServer(signedLinks: false);
    var path = $"/real/v2/testimage/manifests/{UpstreamTestServer.ManifestDigest}";
    var key = GetPathKey(path);

    using (var miss = await server.CreateRequest(path).SendAsync(HttpMethod.Get.Method))
    {
      // The digest-addressed path also proves ':' survives request validation, which used to 400.
      Assert.Equal(HttpStatusCode.RedirectKeepVerb, miss.StatusCode);
      AssertStatusHeader(miss, CachingProxyStatus.MISS);
    }

    Assert.Equal(1, myS3.PutObjectCalls);
    Assert.Equal(UpstreamTestServer.ManifestDigest, myS3.Digests[key]);

    // Read back by a replica that never saw the upstream response, so the header can only have come off
    // the object's metadata and not from an in-memory entry left over from the request above.
    var replica = CreateServer(signedLinks: false);
    using var hit = await replica.CreateRequest(path).SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.OK, hit.StatusCode); // a manifest is small enough to inline
    Assert.Equal(UpstreamTestServer.ManifestDigest,
      hit.Headers.GetValues(CachedResponse.DockerContentDigestHeader).Single());
    Assert.Equal(UpstreamTestServer.ManifestMediaType, hit.Content.Headers.ContentType?.ToString());
    Assert.Equal("{\"layers\":[]}", await hit.Content.ReadAsStringAsync());
    Assert.Equal(1, myS3.PutObjectCalls); // already stored, not re-uploaded
  }

  [Fact]
  public async Task Vary_Accept_Survives_The_Response_Reset()
  {
    // On this backend the client's response is the redirect, and it is written after Response.Clear()
    // wipes everything the request set beforehand - the negotiation announcement included. Cleared and
    // never restored, a shared cache in front of us is free to hand this representation's 307, or a 404,
    // to a client that asked for another media type (MRI-5282).
    var server = CreateServer(signedLinks: false, varyByAccept: true);
    using var miss = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, miss.StatusCode);
    AssertStatusHeader(miss, CachingProxyStatus.MISS);
    Assert.Equal(["Accept"], miss.Headers.Vary);
  }

  [Fact]
  public async Task Path_With_Multiple_Slashes_Is_Bad_Request()
  {
    // A degenerate URL such as "/maven-central////-.jar" leaves a "///-.jar" remainder whose authority is
    // empty, so it does not resolve against the base at all. The shared request validation must reject it as
    // 400 BAD_REQUEST before any S3 work, so the bucket is never probed or written.
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real////-.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.BAD_REQUEST);
    Assert.Equal("Invalid request path", await response.Content.ReadAsStringAsync());
    Assert.Equal(0, myS3.GetObjectCalls);
    Assert.Equal(0, myS3.PutObjectCalls);
  }

  [Fact]
  public async Task Foreign_Authority_Is_Bad_Request()
  {
    // MRI-4844 on the S3 backend: a remainder starting with "//" replaces the authority, so the fetch would
    // go to a host the caller named and be stored under a key derived from it. Rejected before any S3 work,
    // so nothing a caller aimed elsewhere can enter the bucket. Documentation-range address on purpose.
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real///198.51.100.9/x.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.BAD_REQUEST);
    Assert.Equal("Invalid request path", await response.Content.ReadAsStringAsync());
    Assert.Equal(0, myS3.GetObjectCalls);
    Assert.Equal(0, myS3.PutObjectCalls);
  }

  [Fact]
  public async Task Signed_Redirect_Expiry_Uses_Fixed_Ttl()
  {
    // The link is signed on the fly per request, so its expiry is a fixed TTL from "now" and does NOT
    // depend on the cached redirect's lifetime (L1 or L2 duration): Expires = now + SignedLinkTTL.
    // A distinctive 90s TTL makes the assertion fail if the 5-minute default were used instead, and a
    // long L2 OK duration proves the expiry is not sized against the durable cache lifetime.
    var ttl = TimeSpan.FromSeconds(90);
    var server = CreateServer(signedLinks: true, signedLinkTTL: ttl,
      distributedCacheDuration: new CacheDuration { [HttpStatusCode.OK] = TimeSpan.FromMinutes(20) });

    var before = DateTime.UtcNow;
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    var after = DateTime.UtcNow;

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    Assert.NotNull(myS3.LastPresignExpires);
    // GetUtcNow() was sampled during the request, so Expires lands inside the [before, after] window
    // shifted by the TTL.
    Assert.InRange(myS3.LastPresignExpires.Value, before + ttl, after + ttl);
  }

  [Fact]
  public async Task Signed_Redirect_Is_Re_Signed_On_Cache_Hit()
  {
    // The cached redirect stores an unsigned, verb-agnostic Location and is signed on the fly. A second
    // GET is a cache HIT but must still produce a freshly-signed link with a later expiry, so an
    // L2-served redirect never hands the client a stale (cached-at-store-time) URL.
    var ttl = TimeSpan.FromSeconds(90);
    var server = CreateServer(signedLinks: true, signedLinkTTL: ttl);
    myS3.Objects[GetPathKey("/real/a.jar")] = (ourLargeBody, "application/java-archive", null);

    using (var miss = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method))
    {
      Assert.Equal(HttpStatusCode.RedirectKeepVerb, miss.StatusCode);
      AssertStatusHeader(miss, CachingProxyStatus.MISS);
      Assert.Contains("X-Amz-", miss.Headers.Location?.ToString());
    }
    var missExpiry = myS3.LastPresignExpires;
    Assert.NotNull(missExpiry);

    var before = DateTime.UtcNow;
    using var hit = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    var after = DateTime.UtcNow;

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, hit.StatusCode);
    AssertStatusHeader(hit, CachingProxyStatus.HIT);
    // Replayed from cache (no second probe), yet still presigned with a fresh TTL window.
    Assert.Equal(1, myS3.GetObjectCalls);
    Assert.Contains("X-Amz-", hit.Headers.Location?.ToString());
    Assert.NotNull(myS3.LastPresignExpires);
    Assert.InRange(myS3.LastPresignExpires.Value, before + ttl, after + ttl);
  }

  [Fact]
  public async Task Signed_Redirect_Overrides_Cache_Control_For_Anonymous_Request()
  {
    // S3 honours the response-cache-control override only on a signed request, so the presigned link
    // carries it. An anonymous request gets the public, eternally-cacheable value, so the object the
    // client downloads straight from S3 is cached publicly too (not just the proxy's redirect).
    var server = CreateServer(signedLinks: true);
    myS3.Objects[GetPathKey("/real/a.jar")] = (ourLargeBody, "application/java-archive", null);

    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    Assert.Contains("X-Amz-", response.Headers.Location?.ToString()); // presigned
    Assert.Equal("public, max-age=31536000", myS3.LastPresignCacheControl);
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
    Assert.True(myS3.Objects.TryGetValue(GetPathKey("/real/chunked.bin"), out var stored));
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
  public async Task Existing_Large_Object_Redirects_Without_Reupload()
  {
    // An object too large to prefetch inline (> 16 KiB) is redirected, not served from memory.
    var server = CreateServer(signedLinks: true);
    myS3.Objects[GetPathKey("/real/a.jar")] = (ourLargeBody, "application/java-archive", null);
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.Equal(1, myS3.GetObjectCalls); // probed S3
    Assert.Equal(0, myS3.PutObjectCalls); // but did not re-upload
  }

  [Fact]
  public async Task Existing_Small_Object_Is_Served_Inline_From_Memory()
  {
    // An object that fits in the prefetch window is read during the probe and served inline
    // (200 + body) instead of redirecting the client to S3.
    var server = CreateServer(signedLinks: true);
    myS3.Objects[GetPathKey("/real/a.jar")] = ([.. "a.jar"u8], "application/java-archive", null);
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.Null(response.Headers.Location);
    Assert.Equal("a.jar", await response.Content.ReadAsStringAsync());
    Assert.Equal("application/java-archive", response.Content.Headers.ContentType?.ToString());
    Assert.Equal(1, myS3.GetObjectCalls);
    Assert.Equal(0, myS3.PutObjectCalls); // already present, not re-uploaded
  }

  [Fact]
  public async Task Object_Exactly_Prefetch_Window_Size_Is_Inlined()
  {
    // Boundary: an object whose size equals the prefetch window is fully returned by the probe
    // (received bytes == total), so it must be inlined — not redirected. A "last byte < window end"
    // check would wrongly redirect at exactly this size.
    var server = CreateServer(signedLinks: true);
    var exact = new byte[TestInlineThresholdBytes];
    myS3.Objects[GetPathKey("/real/a.jar")] = (exact, "application/java-archive", null);

    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.Null(response.Headers.Location);
    Assert.Equal(exact.Length, (await response.Content.ReadAsByteArrayAsync()).Length);
    Assert.Equal(0, myS3.PutObjectCalls);
  }

  [Fact]
  public async Task Inline_Threshold_Is_Configurable()
  {
    // Raising S3.InlineThresholdBytes widens the inline window: a 32 KiB object that would redirect at
    // the default test window is instead served inline (200 + full body) once the window is 32 KiB.
    var server = CreateServer(signedLinks: false, inlineThresholdBytes: 32 * 1024);
    myS3.Objects[GetPathKey("/real/a.jar")] = (ourLargeBody, "application/java-archive", null);

    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.Null(response.Headers.Location); // inlined, not redirected
    Assert.Equal(ourLargeBody.Length, (await response.Content.ReadAsByteArrayAsync()).Length);
    Assert.Equal(0, myS3.PutObjectCalls); // already in the bucket, served straight from the probe
  }

  [Fact]
  public async Task Inlined_Small_Object_Propagates_ETag()
  {
    // Representation headers from the S3 object (notably ETag) must be replayed to the client.
    var server = CreateServer(signedLinks: true);
    myS3.Objects[GetPathKey("/real/a.jar")] = ([.. "a.jar"u8], "application/java-archive", "\"deadbeef\"");
    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("\"deadbeef\"", response.Headers.ETag?.ToString());
  }

  [Fact]
  public async Task Signed_Head_Inlines_Small_Object()
  {
    // A HEAD prefetches like a GET: a small object is inlined (200 + metadata), not redirected, so
    // its verb-agnostic body can later serve a GET. (Kestrel drops the body for HEAD but keeps the
    // metadata.) No presigned redirect is produced.
    var server = CreateServer(signedLinks: true);
    myS3.Objects[GetPathKey("/real/a.jar")] = ([.. "a.jar"u8], "application/java-archive", "\"deadbeef\"");

    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Head.Method);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.Null(response.Headers.Location);
    Assert.Equal("\"deadbeef\"", response.Headers.ETag?.ToString());
    Assert.Equal(5, response.Content.Headers.ContentLength);
    Assert.Null(myS3.LastPresignVerb);
    Assert.Equal(0, myS3.PutObjectCalls);
  }

  [Fact]
  public async Task Signed_Head_Prefetch_Warms_Cache_For_Following_Get()
  {
    // The inline body is verb-agnostic, so even under signed links a HEAD's prefetch is reused by a
    // following GET from the shared cache entry: the object is probed only once.
    var server = CreateServer(signedLinks: true);
    myS3.Objects[GetPathKey("/real/a.jar")] = ([.. "a.jar"u8], "application/java-archive", null);

    using (var head = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Head.Method))
      Assert.Equal(HttpStatusCode.OK, head.StatusCode);

    using var get = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    AssertStatusHeader(get, CachingProxyStatus.HIT);
    Assert.Equal("a.jar", await get.Content.ReadAsStringAsync());
    Assert.Equal(1, myS3.GetObjectCalls); // HEAD probed; GET replayed from the shared entry
  }

  [Fact]
  public async Task Negative_Result_Is_Shared_Across_Verbs()
  {
    // A negative result has no body and no signature, so it is verb-agnostic: a HEAD that negatively
    // caches a missing object answers a following GET from memory without re-probing S3 or upstream.
    var server = CreateServer(signedLinks: true);

    using (var head = await server.CreateRequest("/real/does-not-exist.jar").SendAsync(HttpMethod.Head.Method))
    {
      Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
      AssertStatusHeader(head, CachingProxyStatus.NEGATIVE_MISS);
    }

    using var get = await server.CreateRequest("/real/does-not-exist.jar").SendAsync(HttpMethod.Get.Method);
    Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    AssertStatusHeader(get, CachingProxyStatus.NEGATIVE_HIT); // served from the shared negative entry
    Assert.Equal(1, myS3.GetObjectCalls);                     // probed once, not re-probed for the GET
  }

  [Fact]
  public async Task Unsigned_Head_Prefetch_Warms_Cache_For_Following_Get()
  {
    // Unsigned links share one verb-agnostic cache key, so the body a HEAD prefetches and inlines is
    // replayed to a following GET from memory: the object is probed/downloaded only once.
    var server = CreateServer(signedLinks: false);
    myS3.Objects[GetPathKey("/real/a.jar")] = ([.. "a.jar"u8], "application/java-archive", null);

    using (var head = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Head.Method))
    {
      Assert.Equal(HttpStatusCode.OK, head.StatusCode);
      AssertStatusHeader(head, CachingProxyStatus.MISS);
    }

    using var get = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    AssertStatusHeader(get, CachingProxyStatus.HIT);
    Assert.Equal("a.jar", await get.Content.ReadAsStringAsync());
    Assert.Equal(1, myS3.GetObjectCalls); // HEAD probed; GET replayed from memory
  }

  [Fact]
  public async Task Inlined_Small_Object_Second_Request_Is_Served_From_Memory_Cache()
  {
    // The inlined body is cached in memory: a second GET must replay it as a HIT without
    // re-probing S3 or re-fetching upstream.
    var server = CreateServer(signedLinks: true);
    myS3.Objects[GetPathKey("/real/a.jar")] = ([.. "a.jar"u8], "application/java-archive", null);

    using (var first = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method))
      AssertStatusHeader(first, CachingProxyStatus.MISS);

    using var second = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    AssertStatusHeader(second, CachingProxyStatus.HIT);
    Assert.Equal("a.jar", await second.Content.ReadAsStringAsync());
    Assert.Equal(1, myS3.GetObjectCalls); // probed only once
    Assert.Equal(0, myS3.PutObjectCalls);
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
  public async Task Head_Serves_Large_Object_Metadata_While_Get_Redirects()
  {
    var server = CreateServer(signedLinks: true);

    // A large object can't be inlined. A HEAD is answered from memory with the full metadata (the
    // Content-Length is the whole object size from Content-Range, not the 16 KiB prefetch slice) and
    // never signs a redirect; a GET is sent its own GET-signed redirect. Independent cache entries.
    myS3.Objects[GetPathKey("/real/a.jar")] = (ourLargeBody, null, "\"big\"");

    using (var head = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Head.Method))
    {
      Assert.Equal(HttpStatusCode.OK, head.StatusCode);
      AssertStatusHeader(head, CachingProxyStatus.MISS);
      Assert.Null(head.Headers.Location);
      Assert.Equal(ourLargeBody.Length, head.Content.Headers.ContentLength);
      Assert.Equal("\"big\"", head.Headers.ETag?.ToString());
      Assert.Null(myS3.LastPresignVerb); // HEAD never signs a redirect
    }

    using var get = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    Assert.Equal(HttpStatusCode.RedirectKeepVerb, get.StatusCode);
    AssertStatusHeader(get, CachingProxyStatus.MISS);
    Assert.Equal(HttpVerb.GET, myS3.LastPresignVerb);
    Assert.Equal(2, myS3.GetObjectCalls); // HEAD and GET each probe (separate entries)
  }

  [Fact]
  public async Task Second_Head_Is_Served_From_Memory_Cache()
  {
    var server = CreateServer(signedLinks: true);

    // A large object: the HEAD is answered with metadata from memory, and the second HEAD replays
    // that metadata from cache without re-probing S3.
    myS3.Objects[GetPathKey("/real/a.jar")] = (ourLargeBody, null, null);

    using (var first = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Head.Method))
    {
      Assert.Equal(HttpStatusCode.OK, first.StatusCode);
      AssertStatusHeader(first, CachingProxyStatus.MISS);
    }

    using var second = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Head.Method);
    Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    AssertStatusHeader(second, CachingProxyStatus.HIT);
    Assert.Equal(1, myS3.GetObjectCalls); // probed once, replayed from memory the second time
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
    var key = GetPathKey("/real/a.jar");
    Assert.EndsWith("/" + key, location);
    Assert.DoesNotContain("//" + key, location);
  }

  [Fact]
  public async Task Encoded_Slash_Is_Stored_Under_Encoded_Key()
  {
    // npm scoped packages arrive with an encoded slash (e.g. @scope%2Fname). The %2F must be
    // preserved when hashing the key (NOT decoded to a real '/'), so the scoped package is keyed
    // distinctly from a real two-segment path "@scope/name". Unsigned links so the Location is
    // a plain bucket URL we can assert on.
    var server = CreateServer(signedLinks: false);
    using var response = await server.CreateRequest("/real/@scope%2fpackage").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS);

    // The encoded slash hashes to a DIFFERENT key than a real slash: the %2F is kept pre-hash
    // (UriFormat.UriEscaped), so "@scope%2fpackage" and "@scope/package" map to distinct objects.
    var key = GetPathKey("/real/@scope%2fpackage");
    Assert.NotEqual(GetPathKey("/real/@scope/package"), key);
    Assert.True(myS3.Objects.TryGetValue(key, out var stored)); // stored under that key, body intact
    Assert.Equal("scoped-package-content", Encoding.UTF8.GetString(stored.Body));

    // The unsigned redirect references that hashed key (pure hex + '/', no escaping needed).
    var location = response.Headers.Location?.ToString();
    Assert.NotNull(location);
    Assert.EndsWith(key, location);
  }

  [Fact]
  public async Task Post_Is_Rejected_Before_Probing_S3()
  {
    var server = CreateServer(signedLinks: true);

    // Regression test: even when the object exists in the bucket, a non-GET/HEAD method must be
    // rejected up front and must NOT be redirected (the validation-bypass fix).
    myS3.Objects[GetPathKey("/real/a.jar")] = ([.. "a.jar"u8], null, null);

    using var response = await server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Post.Method);

    Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    Assert.Equal(0, myS3.GetObjectCalls);
  }

  [Fact]
  public async Task Invalid_Path_Is_Rejected_Before_Probing_S3()
  {
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real/bad~name.jar").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.BAD_REQUEST);
    Assert.Equal(0, myS3.GetObjectCalls);
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

  [Fact]
  public async Task Concurrent_Misses_For_Same_Key_Probe_And_Upload_Once()
  {
    // A thundering herd: many concurrent GETs for the same uncached object. Single-flight coalescing
    // must let exactly ONE request probe S3, fetch upstream and upload; the rest wait and serve the
    // result from the now-populated cache. Without it, every request would probe and upload (the
    // amplification that trips S3 SlowDown).
    var server = CreateServer(signedLinks: true);
    myS3.GateKey = GetPathKey("/real/a.jar"); // block the leader inside its probe

    var tasks = Enumerable.Range(0, 16)
      .Select(_ => server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method))
      .ToArray();

    // Wait until the leader is inside the gated probe (holding the per-key lock), then give the rest
    // of the herd time to pile up on the lock before releasing. Without coalescing they would all be
    // blocked inside the probe instead, driving GetObjectCalls toward 16.
    await myS3.GateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Task.Delay(500);
    myS3.GateRelease.TrySetResult();

    var responses = await Task.WhenAll(tasks);
    try
    {
      foreach (var response in responses)
        Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);

      // Exactly one request did the work (MISS); the rest were coalesced and served from cache (HIT).
      var statuses = responses
        .Select(r => r.Headers.GetValues(CachingProxyConstants.StatusHeader).First())
        .ToList();
      Assert.Equal(1, statuses.Count(s => s == nameof(CachingProxyStatus.MISS)));
      Assert.Equal(responses.Length - 1, statuses.Count(s => s == nameof(CachingProxyStatus.HIT)));

      Assert.Equal(1, myS3.GetObjectCalls); // probed once despite 16 concurrent misses
      Assert.Equal(1, myS3.PutObjectCalls); // uploaded once
    }
    finally
    {
      foreach (var response in responses) response.Dispose();
    }
  }

  [Fact]
  public async Task Concurrent_Misses_For_Different_Keys_Do_Not_Block_Each_Other()
  {
    // The lock must serialize only same-prefix-partition work: a request for an object in a different
    // "aa/bb" prefix must not wait behind a leader busy resolving another. This is only meaningful if
    // the two objects fall in different prefix-partitions (the key is "aa/bb/<hash>", so compare the
    // 5-char prefix).
    Assert.NotEqual(GetPathKey("/real/a.jar")[..5], GetPathKey("/real/extensionless")[..5]);

    var server = CreateServer(signedLinks: true);
    myS3.GateKey = GetPathKey("/real/a.jar"); // stall only a.jar's probe

    var blocked = server.CreateRequest("/real/a.jar").SendAsync(HttpMethod.Get.Method);
    await myS3.GateReached.Task.WaitAsync(TimeSpan.FromSeconds(10)); // a.jar's leader holds its lock

    // A different key resolves end-to-end while a.jar is still gated.
    using (var other = await server.CreateRequest("/real/extensionless").SendAsync(HttpMethod.Get.Method)
             .WaitAsync(TimeSpan.FromSeconds(10)))
    {
      Assert.Equal(HttpStatusCode.RedirectKeepVerb, other.StatusCode);
      AssertStatusHeader(other, CachingProxyStatus.MISS);
    }

    myS3.GateRelease.TrySetResult();
    using var blockedResponse = await blocked;
    Assert.Equal(HttpStatusCode.RedirectKeepVerb, blockedResponse.StatusCode);
  }

  [Fact]
  public async Task Stale_Object_Upstream_Changed_Is_Re_Stored()
  {
    // Past its freshness window, a stale object is revalidated; upstream returns new content (200), so
    // the bucket object is replaced and the client served the refreshed copy (REVALIDATED).
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "old"u8], "text/plain", "\"old\"");
    myS3.CreatedAt[key] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1); // stale
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    upstreamServer.RevalidateContent = "new-content";

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.RedirectKeepVerb, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.REVALIDATED);
    Assert.Equal(1, myS3.PutObjectCalls);
    Assert.Equal("new-content", Encoding.UTF8.GetString(myS3.Objects[key].Body));
  }

  [Fact]
  public async Task Stale_Object_Upstream_NotModified_Is_Kept_And_Touched()
  {
    // Upstream reports 304: the stale object is still valid, so it is kept and served (REVALIDATED),
    // and its stored date is bumped via a metadata-only self-copy (no re-upload).
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"v1\"");
    var staleAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
    myS3.CreatedAt[key] = staleAt;
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotModified;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode); // small object served inline
    AssertStatusHeader(response, CachingProxyStatus.REVALIDATED);
    Assert.Equal("v1", await response.Content.ReadAsStringAsync());
    Assert.Equal(1, myS3.CopyObjectCalls);              // touched
    Assert.Equal(0, myS3.PutObjectCalls);               // not re-uploaded
    Assert.True(myS3.CreatedAt[key] > staleAt);          // freshness clock reset
  }

  [Fact]
  public async Task Stored_Object_Records_The_Upstream_ETag()
  {
    // A MISS records the upstream's validator as user metadata. S3 assigns the object its own ETag (a
    // checksum of the bytes), so the upstream's has to travel alongside it to be usable later.
    var server = CreateServer(signedLinks: false);
    var key = GetPathKey("/real/revalidate.txt");
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    upstreamServer.RevalidateETag = "\"upstream-v1\"";

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.Equal(1, myS3.PutObjectCalls);
    Assert.Equal("\"upstream-v1\"", myS3.UpstreamETags[key]);
  }

  [Fact]
  public async Task Revalidation_Is_Conditional_On_The_Upstream_ETag_Not_The_Bucket_One()
  {
    // The stored object carries both tags: S3's own (meaningless to the upstream) and the upstream's. The
    // conditional request must carry the latter, or the upstream can never answer 304.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"s3-body-md5\"");
    myS3.UpstreamETags[key] = "\"upstream-v1\"";
    myS3.CreatedAt[key] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1); // stale
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotModified;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.REVALIDATED);
    Assert.Equal("\"upstream-v1\"", upstreamServer.RevalidateIfNoneMatch);
    Assert.Equal("\"upstream-v1\"", myS3.UpstreamETags[key]); // and the touch keeps it
    // The client still sees the bucket's tag - unlike the date, which is served as the upstream's. The
    // tag is never the upstream's to hand out: a 307'd client is answered by S3 matching its own.
    Assert.Equal("\"s3-body-md5\"", response.Headers.ETag?.ToString());
  }

  [Fact]
  public async Task Revalidation_Sends_No_ETag_When_None_Was_Recorded()
  {
    // Nothing recorded - an upstream that issues no ETag, or an object stored before this was recorded -
    // so the request is conditional on the stored date alone. Sending the bucket's own tag instead would
    // make the origin skip the date it can actually evaluate.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"s3-body-md5\"");
    myS3.CreatedAt[key] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
    Assert.DoesNotContain(key, myS3.UpstreamETags.Keys);
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotModified;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.REVALIDATED);
    Assert.Null(upstreamServer.RevalidateIfNoneMatch);
    Assert.NotNull(upstreamServer.RevalidateIfModifiedSince); // the date still is a precondition
  }

  [Fact]
  public async Task Stored_Object_From_An_ETagless_Upstream_Records_No_ETag()
  {
    // An upstream that issues no ETag (nginx origins behind redirector.kotlinlang.org, for one) must
    // leave the key unset rather than have S3's checksum stand in for it - what such an object then
    // revalidates on is Revalidation_Sends_No_ETag_When_None_Was_Recorded.
    var server = CreateServer(signedLinks: false);
    var key = GetPathKey("/real/revalidate.txt");
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    upstreamServer.RevalidateETag = null;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.Equal(1, myS3.PutObjectCalls);
    Assert.Null(myS3.UpstreamETags[key]);
  }

  [Fact]
  public async Task Revalidation_That_Re_Stores_Records_The_Fresh_Upstream_ETag()
  {
    // The upstream answers 200 (changed), so the re-store replaces the recorded validator with the one
    // the new bytes came under - otherwise the next revalidation would ask about the previous body.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "old"u8], "text/plain", "\"s3-body-md5\"");
    myS3.UpstreamETags[key] = "\"upstream-v1\"";
    myS3.CreatedAt[key] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    upstreamServer.RevalidateContent = "new-content";
    upstreamServer.RevalidateETag = "\"upstream-v2\"";

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.REVALIDATED);
    Assert.Equal("\"upstream-v1\"", upstreamServer.RevalidateIfNoneMatch); // asked about the old body
    Assert.Equal("\"upstream-v2\"", myS3.UpstreamETags[key]);              // recorded the new one
  }

  [Fact]
  public async Task Stored_Object_Records_The_Upstream_Last_Modified()
  {
    // A MISS records the upstream's date as user metadata, for the same reason as its ETag: the object's
    // own LastModified is when S3 took the bytes, which says nothing about when the entity changed.
    var server = CreateServer(signedLinks: false);
    var key = GetPathKey("/real/revalidate.txt");
    var upstreamDate = new DateTimeOffset(2025, 4, 27, 15, 7, 12, TimeSpan.Zero);
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    upstreamServer.RevalidateLastModified = upstreamDate.ToString("R");

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.MISS);
    Assert.Equal(1, myS3.PutObjectCalls);
    Assert.Equal(upstreamDate, myS3.UpstreamLastModified[key]);
  }

  [Fact]
  public async Task Served_Object_Reports_The_Upstream_Last_Modified()
  {
    // What a MISS advertised, a HIT advertises too. S3's store time would differ from it for the same
    // bytes, and the metadata-only touch moves that time on every 304, so a client watching the date
    // would see the entity change without its content changing.
    var server = CreateServer(signedLinks: false);
    var key = GetPathKey("/real/revalidate.txt");
    var upstreamDate = new DateTimeOffset(2025, 4, 27, 15, 7, 12, TimeSpan.Zero);
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"s3-body-md5\"");
    myS3.UpstreamLastModified[key] = upstreamDate;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.MISS); // served from the bucket, upstream not consulted
    Assert.Equal(upstreamDate, response.Content.Headers.LastModified);
  }

  [Fact]
  public async Task Served_Object_Falls_Back_To_The_Bucket_Date_When_No_Upstream_Date_Was_Recorded()
  {
    // An object stored before the date was recorded still gets a Last-Modified, S3's own. It is later than
    // the entity's, which costs a client nothing here: this path answers no conditional request, and one
    // sent to the bucket by a 307 is judged against that same S3 date.
    var server = CreateServer(signedLinks: false);
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"s3-body-md5\"");
    Assert.DoesNotContain(key, myS3.UpstreamLastModified.Keys);

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.MISS);
    var served = Assert.IsType<DateTimeOffset>(response.Content.Headers.LastModified);
    Assert.True(DateTimeOffset.UtcNow - served < TimeSpan.FromMinutes(1), $"expected the bucket's store time, got {served:O}");
  }

  [Fact]
  public async Task Revalidation_Is_Conditional_On_The_Upstream_Date_Not_Our_Store_Date()
  {
    // Our store date would work most of the time, but it is later than the upstream's: a change made
    // while we were downloading precedes it, so the origin would answer 304 for the version we missed and
    // the touch would push the date forward again, freezing the object. The upstream's own date has no
    // such window.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    var upstreamDate = new DateTimeOffset(2025, 4, 27, 15, 7, 12, TimeSpan.Zero);
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"s3-body-md5\"");
    myS3.UpstreamLastModified[key] = upstreamDate;
    myS3.CreatedAt[key] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1); // stale
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotModified;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.REVALIDATED);
    Assert.Equal(upstreamDate.ToString("R"), upstreamServer.RevalidateIfModifiedSince);
    Assert.Equal(upstreamDate, myS3.UpstreamLastModified[key]); // and the touch keeps it
    // The kept object is served under the same date, not the one the touch just moved.
    Assert.Equal(upstreamDate, response.Content.Headers.LastModified);
  }

  [Fact]
  public async Task Revalidation_Falls_Back_To_The_Stored_Date_When_No_Upstream_Date_Was_Recorded()
  {
    // Nothing recorded - an upstream that sends no Last-Modified, or an object stored before this was
    // recorded - so the request is conditional on our own store date. That is sound, being never earlier
    // than the upstream's: an unchanged object still 304s and a changed one still gets a full body.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    var storedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"s3-body-md5\"");
    myS3.CreatedAt[key] = storedAt;
    Assert.DoesNotContain(key, myS3.UpstreamLastModified.Keys);
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotModified;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.REVALIDATED);
    Assert.Equal(storedAt.ToString("R"), upstreamServer.RevalidateIfModifiedSince);
  }

  [Fact]
  public async Task Revalidation_That_Re_Stores_Records_The_Fresh_Upstream_Date()
  {
    // The upstream answers 200 (changed), so the re-store replaces the recorded date with the new bytes'
    // - otherwise the next revalidation would ask about the previous body's date and be told 304.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    var oldDate = new DateTimeOffset(2025, 4, 27, 15, 7, 12, TimeSpan.Zero);
    var newDate = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);
    myS3.Objects[key] = ([.. "old"u8], "text/plain", "\"s3-body-md5\"");
    myS3.UpstreamLastModified[key] = oldDate;
    myS3.CreatedAt[key] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.Ok;
    upstreamServer.RevalidateContent = "new-content";
    upstreamServer.RevalidateLastModified = newDate.ToString("R");

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    AssertStatusHeader(response, CachingProxyStatus.REVALIDATED);
    Assert.Equal(oldDate.ToString("R"), upstreamServer.RevalidateIfModifiedSince); // asked about the old body
    Assert.Equal(newDate, myS3.UpstreamLastModified[key]);                         // recorded the new one
  }

  [Fact]
  public async Task Dateless_Object_Is_Revalidated_And_Gains_A_Stored_Date()
  {
    // An object stored before "created-at" existed: with no stored date the freshness window cannot be
    // measured, so the object counts as stale instead of fresh forever. The 304 touch writes the key,
    // so it is measurable from then on.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"v1\"");
    Assert.DoesNotContain(key, myS3.CreatedAt.Keys); // no freshness clock at all
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotModified;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.REVALIDATED);
    Assert.Equal("v1", await response.Content.ReadAsStringAsync());
    Assert.Equal(1, myS3.CopyObjectCalls);           // touched
    Assert.Equal(0, myS3.PutObjectCalls);            // not re-uploaded
    Assert.Contains(key, myS3.CreatedAt.Keys);       // healed: the clock exists now
  }

  [Fact]
  public async Task Dateless_Object_Under_An_Immutable_Rule_Is_Served_As_Is()
  {
    // Without a freshness window there is nothing to measure and nothing to heal, so a dateless object
    // is served untouched - the upstream is not consulted at all.
    var server = CreateServer(signedLinks: false);
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"v1\"");
    var upstreamHits = upstreamServer.RevalidateRequestCount; // shared fixture, so count the delta

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.MISS); // fresh from the bucket, no upstream call
    Assert.Equal(upstreamHits, upstreamServer.RevalidateRequestCount);
    Assert.Equal(0, myS3.CopyObjectCalls);
    Assert.Equal(0, myS3.PutObjectCalls);
  }

  [Fact]
  public async Task Stale_Object_Upstream_Error_Serves_Stale_Copy()
  {
    // Upstream is unreachable/5xx during revalidation: the stale object must still be served (STALE)
    // and left untouched in the bucket.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"v1\"");
    myS3.CreatedAt[key] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.ServerError;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.STALE);
    Assert.Equal("v1", await response.Content.ReadAsStringAsync());
    Assert.Equal(0, myS3.PutObjectCalls);
    Assert.Equal(0, myS3.DeleteObjectCalls);
    Assert.True(myS3.Objects.ContainsKey(key)); // kept
  }

  [Fact]
  public async Task Stale_Object_Upstream_NotFound_Is_Deleted()
  {
    // Upstream returns 404 during revalidation: the stale object is removed from the bucket and the
    // client gets a (negatively cached) 404.
    var server = CreateServer(signedLinks: false, refreshAfter: TimeSpan.FromMinutes(1));
    var key = GetPathKey("/real/revalidate.txt");
    myS3.Objects[key] = ([.. "v1"u8], "text/plain", "\"v1\"");
    myS3.CreatedAt[key] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
    upstreamServer.Revalidate = UpstreamTestServer.RevalidateBehavior.NotFound;

    using var response = await server.CreateRequest("/real/revalidate.txt").SendAsync(HttpMethod.Get.Method);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    AssertStatusHeader(response, CachingProxyStatus.NEGATIVE_MISS);
    Assert.Equal(1, myS3.DeleteObjectCalls);
    Assert.False(myS3.Objects.ContainsKey(key)); // removed
  }

  private string GetPathKey(string path)
  {
    Assert.StartsWith(myRemoteServer.Prefix, path);
    var upstream = myRemoteServer.GetUpstreamUri(path[myRemoteServer.Prefix.Value!.Length..]);
    Assert.NotNull(upstream); // asking for the key of a path that has no upstream is a test bug
    return upstream.ManglePath();
  }

  private static void AssertStatusHeader(HttpResponseMessage response, CachingProxyStatus status) =>
    Assert.Equal(status.ToString(), response.Headers.GetValues(CachingProxyConstants.StatusHeader).First());

  Task IAsyncLifetime.InitializeAsync()
  {
    Environment.SetEnvironmentVariable("SENTRY_RELEASE", "release@1.0.0");
    // The upstream is a class fixture, so its validator knobs would otherwise carry into the next test.
    upstreamServer.RevalidateETag = null;
    upstreamServer.RevalidateIfNoneMatch = null;
    upstreamServer.RevalidateLastModified = null;
    upstreamServer.RevalidateIfModifiedSince = null;
    return Task.CompletedTask;
  }

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
    new BasicAWSCredentials("test", "test"), new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1 }), IAmazonS3
  {
    public readonly Dictionary<string, (byte[] Body, string? ContentType, string? ETag)> Objects = new();
    // The "uri" user-metadata stored alongside each PutObject, keyed by object key.
    public readonly Dictionary<string, string?> PutObjectUris = new();
    // The "docker-content-digest" user-metadata, keyed by object key. Written by PutObject and replayed
    // by GetObject, like the real bucket - an OCI digest cannot be recomputed from the stored bytes.
    public readonly Dictionary<string, string?> Digests = new();
    // The object's "created-at" user-metadata (our freshness clock), keyed by object key. Absent
    // unless written by a PutObject/CopyObject or seeded by a test; revalidation tests backdate it
    // to make the object appear stale.
    public readonly Dictionary<string, DateTimeOffset> CreatedAt = new();
    // The "upstream-etag" user-metadata (the upstream's validator for the stored bytes), keyed by object
    // key. Distinct from the object's own ETag in Objects, which is S3's - that difference is the point.
    public readonly Dictionary<string, string?> UpstreamETags = new();
    // The "upstream-last-modified" user-metadata (the upstream's date for the stored bytes), keyed by
    // object key. Distinct from the response's LastModified below, which is S3's own store time.
    public readonly Dictionary<string, DateTimeOffset?> UpstreamLastModified = new();
    // S3 existence probes via the ranged GetObject prefetch.
    public int GetObjectCalls;
    public int PutObjectCalls;
    public int CopyObjectCalls;
    public int DeleteObjectCalls;
    public int UploadPartCalls;
    public int AbortMultipartUploadCalls;

    // In-flight multipart uploads: uploadId -> the initiate request (which carries the metadata the
    // completed object gets) plus the parts received so far.
    private readonly Dictionary<string, (InitiateMultipartUploadRequest Request, SortedDictionary<int, byte[]> Parts)> myMultipartUploads = new();

    // Part number whose upload should fail, standing in for a transient S3 error mid-upload.
    public int? FailUploadPartNumber;

    // Sizes of the parts of the last completed multipart upload, in the order they were assembled.
    public IReadOnlyList<int> CompletedPartSizes = [];
    public HttpVerb? LastPresignVerb;
    public DateTime? LastPresignExpires;
    public string? LastPresignCacheControl;

    // Test gate for forcing concurrent probes to overlap: when GateKey is set, a probe for that key
    // signals GateReached and then blocks on GateRelease before continuing. Lets a test pile up a
    // herd of concurrent requests behind the leader and assert how many actually reach S3.
    public string? GateKey;
    public readonly TaskCompletionSource GateReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource GateRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    string IAmazonS3.GetPreSignedURL(GetPreSignedUrlRequest request)
    {
      LastPresignVerb = request.Verb;
      LastPresignExpires = request.Expires;
      LastPresignCacheControl = request.ResponseHeaderOverrides?.CacheControl;
      return base.GetPreSignedURL(request);
    }

    Task<string> IAmazonS3.GetPreSignedURLAsync(GetPreSignedUrlRequest request)
    {
      LastPresignVerb = request.Verb;
      LastPresignExpires = request.Expires;
      LastPresignCacheControl = request.ResponseHeaderOverrides?.CacheControl;
      return base.GetPreSignedURLAsync(request);
    }

    // The middleware probes with a ranged GET (prefetch of the first bytes): on a small enough object
    // the whole body fits in the range and is served inline; otherwise it redirects. Honour the
    // requested ByteRange and report the slice with a 206 + Content-Range, exactly as S3 does.
    public override async Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref GetObjectCalls);
      if (GateKey != null && request.Key == GateKey)
      {
        GateReached.TrySetResult();
        await GateRelease.Task.WaitAsync(cancellationToken);
      }

      if (!Objects.TryGetValue(request.Key, out var obj))
        throw new AmazonS3Exception(nameof(HttpStatusCode.NotFound)) { StatusCode = HttpStatusCode.NotFound };

      var total = obj.Body.Length;
      var start = (int)request.ByteRange.Start;
      var lastIndex = Math.Min((int)request.ByteRange.End, total - 1);
      var length = lastIndex - start + 1;
      var slice = new byte[length];
      Array.Copy(obj.Body, start, slice, 0, length);

      var response = new GetObjectResponse
      {
        HttpStatusCode = HttpStatusCode.PartialContent,
        ResponseStream = new MemoryStream(slice),
        ContentLength = length,
        ContentRange = $"bytes {start}-{lastIndex}/{total}",
        ETag = obj.ETag,
        LastModified = DateTime.UtcNow,
      };
      response.Headers.ContentType = obj.ContentType;
      if (CreatedAt.TryGetValue(request.Key, out var createdAt))
        response.Metadata["created-at"] = createdAt.ToString("O");
      if (Digests.GetValueOrDefault(request.Key) is { } digest)
        response.Metadata[CachedResponse.DockerContentDigestMetadataKey] = digest;
      if (UpstreamETags.GetValueOrDefault(request.Key) is { } upstreamETag)
        response.Metadata["upstream-etag"] = upstreamETag;
      if (UpstreamLastModified.GetValueOrDefault(request.Key) is { } upstreamLastModified)
        response.Metadata["upstream-last-modified"] = upstreamLastModified.ToString("O");
      return response;
    }

    public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref PutObjectCalls);
      // What real S3 does, and the whole reason the multipart path exists (MRI-5371): a single PUT this
      // big is refused, so a regression that routes a huge object back here fails instead of passing.
      if (request.Headers.ContentLength >= SinglePutLimitBytes)
        throw new AmazonS3Exception("Your proposed upload exceeds the maximum allowed size")
        {
          ErrorCode = "EntityTooLarge", StatusCode = HttpStatusCode.BadRequest,
        };

      using var ms = new MemoryStream();
      await request.InputStream.CopyToAsync(ms, cancellationToken);
      Store(request.Key, ms.ToArray(), request.Headers, request.Metadata);
      return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK };
    }

    public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
    {
      var uploadId = "upload-" + Guid.NewGuid();
      myMultipartUploads[uploadId] = (request, new SortedDictionary<int, byte[]>());
      return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = uploadId, HttpStatusCode = HttpStatusCode.OK });
    }

    public override async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref UploadPartCalls);
      if (request.PartNumber == FailUploadPartNumber)
        throw new AmazonS3Exception("Please reduce your request rate.")
        {
          ErrorCode = "SlowDown", StatusCode = HttpStatusCode.ServiceUnavailable,
        };

      using var ms = new MemoryStream();
      await request.InputStream.CopyToAsync(ms, cancellationToken);
      var part = ms.ToArray();
      // A part stream that does not hold exactly PartSize bytes means the caller mis-sliced the body,
      // which S3 would accept and store as a corrupt object.
      Assert.Equal(request.PartSize, part.LongLength);
      myMultipartUploads[request.UploadId].Parts[request.PartNumber!.Value] = part;
      return new UploadPartResponse
      {
        PartNumber = request.PartNumber, ETag = $"\"part-{request.PartNumber}\"", HttpStatusCode = HttpStatusCode.OK,
      };
    }

    public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
    {
      var upload = myMultipartUploads[request.UploadId];
      // Assembled in the order the caller listed the parts, not in part-number order: a caller that
      // lists them out of order has to show up as a wrong body rather than as a silent success.
      var ordered = request.PartETags.Select(tag => upload.Parts[tag.PartNumber!.Value]).ToList();
      CompletedPartSizes = ordered.Select(static part => part.Length).ToList();
      Store(request.Key, ordered.SelectMany(static part => part).ToArray(), upload.Request.Headers, upload.Request.Metadata);
      myMultipartUploads.Remove(request.UploadId);
      return Task.FromResult(new CompleteMultipartUploadResponse { HttpStatusCode = HttpStatusCode.OK });
    }

    public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref AbortMultipartUploadCalls);
      myMultipartUploads.Remove(request.UploadId);
      return Task.FromResult(new AbortMultipartUploadResponse { HttpStatusCode = HttpStatusCode.OK });
    }

    // Shared by the single-PUT and the multipart completion, so a test cannot tell the two apart by the
    // object they leave behind - which is the point of the multipart path.
    private void Store(string key, byte[] body, HeadersCollection headers, MetadataCollection metadata)
    {
      Objects[key] = (body, headers.ContentType, null);
      PutObjectUris[key] = metadata["uri"];
      Digests[key] = metadata[CachedResponse.DockerContentDigestMetadataKey];
      UpstreamETags[key] = metadata["upstream-etag"];
      UpstreamLastModified[key] = ParseMetadataDate(metadata["upstream-last-modified"]);
      if (metadata["created-at"] is { } createdAt)
        CreatedAt[key] = DateTimeOffset.Parse(createdAt);
    }

    // Metadata-only self-copy used to bump an object's "created-at" (its freshness clock) on a 304.
    public override Task<CopyObjectResponse> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref CopyObjectCalls);
      // Assigned unconditionally: the copy re-applies the whole metadata set, so a key it drops must
      // read back as dropped rather than as its stale value.
      UpstreamETags[request.DestinationKey] = request.Metadata["upstream-etag"];
      UpstreamLastModified[request.DestinationKey] = ParseMetadataDate(request.Metadata["upstream-last-modified"]);
      if (request.Metadata["created-at"] is { } createdAt)
        CreatedAt[request.DestinationKey] = DateTimeOffset.Parse(createdAt);
      return Task.FromResult(new CopyObjectResponse { HttpStatusCode = HttpStatusCode.OK });
    }

    private static DateTimeOffset? ParseMetadataDate(string? value) =>
      value is { Length: > 0 } ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

    public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref DeleteObjectCalls);
      Objects.Remove(request.Key);
      CreatedAt.Remove(request.Key);
      UpstreamETags.Remove(request.Key);
      UpstreamLastModified.Remove(request.Key);
      return Task.FromResult(new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.OK });
    }

    public override Task<GetBucketAclResponse> GetBucketAclAsync(GetBucketAclRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new GetBucketAclResponse { HttpStatusCode = HttpStatusCode.OK });
  }
}
