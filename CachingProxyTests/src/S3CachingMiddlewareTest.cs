using System;
using System.Collections.Generic;
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

  private readonly FakeAmazonS3 myS3 = new();
  private readonly List<IHost> myHosts = [];
  private readonly RemoteServers.RemoteServer myRemoteServer = new("/real", upstreamServer.Url, new CacheDuration());

  private TestServer CreateServer(bool signedLinks, TimeSpan? signedLinkTTL = null, int inlineThresholdBytes = TestInlineThresholdBytes,
    CacheDuration? distributedCacheDuration = null)
  {
    var config = new CachingProxyConfig
    {
      S3 = new CachingProxyConfig.S3Config(BucketName, signedLinks)
      {
        SignedLinkTTL = signedLinkTTL ?? TimeSpan.FromMinutes(10),
        InlineThresholdBytes = inlineThresholdBytes,
      },
      DistributedCacheDuration = distributedCacheDuration ?? new CacheDuration(),
      Prefixes = [$"/real={upstreamServer.Url}"],
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
          .ConfigureServices((context, services) =>
          {
            services
              .AddSingleton(config)
              .ConfigureOurServices(context.Configuration)
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
    Assert.Equal(myRemoteServer.GetUpstreamUri("a.jar").ToString(), storedUri);
  }

  [Fact]
  public async Task Path_With_Multiple_Slashes_Is_Bad_Request()
  {
    // A degenerate URL such as "/maven-central////-.jar" resolves the "///-.jar" remainder to an
    // empty authority (invalid for http(s)). The shared request validation must reject it as 400
    // BAD_REQUEST before any S3 work, so the bucket is never probed or written.
    var server = CreateServer(signedLinks: true);
    using var response = await server.CreateRequest("/real////-.jar").SendAsync(HttpMethod.Get.Method);

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

  private string GetPathKey(string path)
  {
    Assert.StartsWith(myRemoteServer.Prefix, path);
    return myRemoteServer.GetUpstreamUri(path[myRemoteServer.Prefix.Value!.Length..]).ManglePath();
  }

  private static void AssertStatusHeader(HttpResponseMessage response, CachingProxyStatus status) =>
    Assert.Equal(status.ToString(), response.Headers.GetValues(CachingProxyConstants.StatusHeader).First());

  Task IAsyncLifetime.InitializeAsync()
  {
    Environment.SetEnvironmentVariable("SENTRY_RELEASE", "release@1.0.0");
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
    // S3 existence probes via the ranged GetObject prefetch.
    public int GetObjectCalls;
    public int PutObjectCalls;
    public HttpVerb? LastPresignVerb;
    public DateTime? LastPresignExpires;

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
      return base.GetPreSignedURL(request);
    }

    Task<string> IAmazonS3.GetPreSignedURLAsync(GetPreSignedUrlRequest request)
    {
      LastPresignVerb = request.Verb;
      LastPresignExpires = request.Expires;
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
      };
      response.Headers.ContentType = obj.ContentType;
      return response;
    }

    public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref PutObjectCalls);
      using var ms = new MemoryStream();
      await request.InputStream.CopyToAsync(ms, cancellationToken);
      Objects[request.Key] = (ms.ToArray(), request.Headers.ContentType, null);
      PutObjectUris[request.Key] = request.Metadata["uri"];
      return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK };
    }

    public override Task<GetBucketAclResponse> GetBucketAclAsync(GetBucketAclRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new GetBucketAclResponse { HttpStatusCode = HttpStatusCode.OK });
  }
}
