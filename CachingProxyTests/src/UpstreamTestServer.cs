using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

[SuppressMessage("ReSharper", "UnusedParameter.Local")]
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public class UpstreamTestServer : IAsyncLifetime
{
  public Uri Url =>
    field ??= new Uri(myWebApp
      .Services.GetRequiredService<IServer>()
      .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

  public string LastUserAgent { get; private set; } = "";

  // The exact origin-form target the proxy emitted (= the PathAndQuery the proxy's HttpClient
  // sent), so tests can assert what the upstream actually received on the wire.
  public string LastRawTarget { get; private set; } = "";

  private readonly WebApplication myWebApp;
  public volatile bool Conditional500SendErrorOnce;

  // Drives the "revalidate.txt" route the freshness/revalidation tests use to script how the upstream
  // answers a conditional revalidation: serve content (200), say unchanged (304), be gone (404) or
  // fail (500). RevalidateContent is the 200 body; RevalidateRequestCount counts route hits.
  public enum RevalidateBehavior { Ok, NotModified, NotFound, ServerError }
  public volatile RevalidateBehavior Revalidate = RevalidateBehavior.Ok;
  public volatile string RevalidateContent = "v1";
  public int RevalidateRequestCount;

  // Raw Last-Modified header (RFC-1123) the revalidate route reports, or null to send none. Kept as a
  // string so it can be volatile like the knobs above. Tests set it to a date far in the past to check
  // that the proxy's freshness window is anchored to its own stored date rather than the upstream's -
  // while the conditional request that window opens is anchored to this one, the entity's own date.
  public volatile string? RevalidateLastModified;

  // The ETag the revalidate route reports, or null to send none, and the conditional headers of the last
  // request to it. Together they check that the tag a cache entry was stored under is the one the next
  // revalidation is conditional on - a cache that sends its storage layer's tag instead never gets a 304,
  // and an origin that cannot match an If-None-Match ignores If-Modified-Since too (RFC 9110 13.2.2), so
  // a fabricated tag is worse than none.
  public volatile string? RevalidateETag;
  public volatile string? RevalidateIfNoneMatch;
  public volatile string? RevalidateIfModifiedSince;

  // Counts hits on the maven metadata / snapshot routes used by the caching-profile tests. The
  // maven-metadata route is conditional-aware (a request carrying If-Modified-Since / If-None-Match
  // gets a 304), so a revalidation after the freshness window is served as REVALIDATED (kept).
  public int MavenMetadataRequestCount;
  public int SnapshotRequestCount;

  // Counts hits on the npm packument / tarball routes. The packument route is conditional-aware and,
  // on a conditional request, honours the shared Revalidate behavior so the npm tests can script a
  // 304 (kept) or a 5xx (serve stale) after the freshness window.
  public int PackumentRequestCount;
  public int TarballRequestCount;

  // Counts hits on the NuGet v3 flat-container routes. The version list is conditional-aware, like the
  // npm packument; the .nupkg route is not, because immutable content is never revalidated.
  public int NugetVersionListRequestCount;
  public int NugetPackageRequestCount;

  // How long the /slow.txt route stalls before answering, so a test can put RequestTimeoutSec on either
  // side of the upstream's own latency. The stall precedes the response head, which is the only phase
  // that budget covers.
  public volatile int SlowResponseDelayMs = 1500;

  // Shape of the /slow-body.bin route: a complete head with a declared Content-Length, then the body
  // dribbled out in chunks. That is a large layer on a slow link - the phase RequestTimeoutSec does not
  // reach and IdleReadTimeoutSec does, with the chunk delay deciding which of the two a test exercises.
  public volatile int SlowBodyChunks = 20;
  public volatile int SlowBodyChunkDelayMs = 200;
  public const int SlowBodyChunkSize = 1024;

  // Counts hits on the OCI distribution routes. The manifest-by-tag route is conditional-aware and
  // honours the shared Revalidate behavior, like the npm packument. ManifestAcceptHeaders records the
  // Accept of every manifest request in arrival order, so the tests can assert the client's own Accept
  // reached the upstream (and not a proxy-invented one).
  public int ManifestByTagRequestCount;
  public int ManifestByDigestRequestCount;
  public int BlobRequestCount;
  public int TokenRequestCount;
  public readonly ConcurrentQueue<string> ManifestAcceptHeaders = new();

  // Counts hits on the PEP 503/691 simple-index route and records the Accept of each, in arrival order,
  // so a test can assert that two representations of one URL cost two upstream fetches and no more - and
  // that what negotiated was the client's own Accept rather than one the proxy invented.
  public int SimpleIndexRequestCount;
  public readonly ConcurrentQueue<string> SimpleAcceptHeaders = new();

  public const string SimpleJsonMediaType = "application/vnd.pypi.simple.v1+json";

  // Set to make every OCI route answer 401 + a Bearer challenge unless the request carries
  // "Authorization: Bearer <TokenToIssue>", so a test can drive the full challenge/token/retry dance.
  // The realm points back at this server's own /oauth/token route.
  public volatile bool RequireRegistryToken;
  public volatile string TokenToIssue = "test-registry-token";

  // The last Authorization the token route received, or "" for an anonymous token request. The whole
  // point of the service-account model is that a client's own credentials never reach an upstream, so a
  // test asserts on this rather than trusting the code path.
  public volatile string LastTokenRequestAuthorization = "";

  // Digest of the manifest body below, as the upstream reports it in Docker-Content-Digest. A fixed
  // literal, not a hash of the body: a real registry's digest covers the exact stored bytes, which is
  // precisely why the proxy has to carry the header through rather than recompute it.
  public const string ManifestDigest = "sha256:1e9f7c94ac5b1e1cb1ee5cf1f2a4e6d7c8b90a1234567890abcdef1234567890";
  public const string BlobDigest = "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
  public const string IndexMediaType = "application/vnd.oci.image.index.v1+json";
  public const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";

  public UpstreamTestServer()
  {
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
    myWebApp = builder.Build();

    myWebApp
      .Use((context, next) =>
      {
        LastUserAgent = context.Request.Headers.UserAgent.ToString();
        LastRawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";

        // A real registry gates the whole /v2/ tree, not individual routes, and answers with the
        // challenge that names where a token comes from. Modelled here so the proxy's token dance is
        // exercised end to end rather than per route.
        if (RequireRegistryToken && context.Request.Path.StartsWithSegments("/v2") &&
            context.Request.Headers.Authorization.ToString() != $"Bearer {TokenToIssue}")
        {
          context.Response.StatusCode = StatusCodes.Status401Unauthorized;
          // Deliberately in the wire shape Docker Hub uses: several comma-separated auth-params whose
          // quoted values themselves contain commas' neighbours ('/' and ':'). This is what the framework's
          // own WWW-Authenticate parser mangles, hence RegistryTokenProvider's hand parser.
          context.Response.Headers.WWWAuthenticate =
            $"Bearer realm=\"{Url}oauth/token\",service=\"upstream-test\",scope=\"repository:testimage:pull\"";
          return context.Response.WriteAsync("{\"errors\":[{\"code\":\"UNAUTHORIZED\"}]}");
        }

        return next(context);
      })
      .UseRouter(router => router
      .MapGet("oauth/token", (req, res, data) =>
      {
        Interlocked.Increment(ref TokenRequestCount);
        LastTokenRequestAuthorization = req.Headers.Authorization.ToString();
        res.ContentType = MediaTypeNames.Application.Json;
        // expires_in is short but longer than RegistryTokenProvider's refresh skew, so the token is
        // cached rather than re-minted on the very next request.
        return res.WriteAsync($"{{\"token\":\"{TokenToIssue}\",\"expires_in\":300}}");
      })
      .MapGet("v2/testimage/manifests/24.04", (req, res, data) =>
      {
        Interlocked.Increment(ref ManifestByTagRequestCount);
        var accept = req.Headers.Accept.ToString();
        ManifestAcceptHeaders.Enqueue(accept);

        // Conditional revalidation after the freshness window: honour the scripted behavior, like the
        // npm packument route.
        if (req.Headers.ContainsKey("If-Modified-Since") || req.Headers.ContainsKey("If-None-Match"))
        {
          switch (Revalidate)
          {
            case RevalidateBehavior.NotModified:
              res.StatusCode = StatusCodes.Status304NotModified;
              return Task.CompletedTask;
            case RevalidateBehavior.NotFound:
              res.StatusCode = StatusCodes.Status404NotFound;
              return res.WriteAsync("{\"errors\":[{\"code\":\"MANIFEST_UNKNOWN\"}]}");
            case RevalidateBehavior.ServerError:
              res.StatusCode = StatusCodes.Status500InternalServerError;
              return res.WriteAsync("boom");
          }
        }

        // A strict registry - ghcr, and the OCI spec's reading - answers 404 with the error document when
        // it holds none of the media types asked for; a lenient one (Docker Hub) hands the index back
        // whatever the Accept says. Strictness is why the Accept set is part of the answer, and so has to
        // be part of every cache key the answer is stored under, negative entries included (MRI-5282).
        // No Accept at all, or a wildcard one, means the client takes what there is.
        if (accept.Length > 0 && !accept.Contains('*') &&
            !accept.Contains(IndexMediaType, StringComparison.OrdinalIgnoreCase) &&
            !accept.Contains(ManifestMediaType, StringComparison.OrdinalIgnoreCase))
        {
          res.StatusCode = StatusCodes.Status404NotFound;
          res.ContentType = MediaTypeNames.Application.Json;
          return res.WriteAsync("{\"errors\":[{\"code\":\"MANIFEST_UNKNOWN\"}]}");
        }

        // The negotiation itself: a client that accepts an index gets one, everyone else gets a
        // single-arch manifest. Same URL, two representations, which is what VaryByAccept is for.
        var wantsIndex = accept.Contains(IndexMediaType, StringComparison.OrdinalIgnoreCase);
        res.ContentType = wantsIndex ? IndexMediaType : ManifestMediaType;
        res.Headers[CachedResponse.DockerContentDigestHeader] = ManifestDigest;
        return res.WriteAsync(wantsIndex ? "{\"manifests\":[]}" : "{\"layers\":[]}");
      })
      .MapGet($"v2/testimage/manifests/{ManifestDigest}", (req, res, data) =>
      {
        Interlocked.Increment(ref ManifestByDigestRequestCount);
        ManifestAcceptHeaders.Enqueue(req.Headers.Accept.ToString());
        res.ContentType = ManifestMediaType;
        res.Headers[CachedResponse.DockerContentDigestHeader] = ManifestDigest;
        return res.WriteAsync("{\"layers\":[]}");
      })
      .MapGet($"v2/testimage/blobs/{BlobDigest}", (req, res, data) =>
      {
        Interlocked.Increment(ref BlobRequestCount);
        res.ContentType = MediaTypeNames.Application.Octet;
        res.Headers[CachedResponse.DockerContentDigestHeader] = BlobDigest;
        return res.WriteAsync("blob-content");
      })
      .MapGet("slow.txt", async (req, res, data) =>
      {
        // Bail out on the abort the proxy raises when its budget expires, rather than writing into a dead
        // connection and surfacing as a Kestrel error in the middle of an otherwise passing test.
        try
        {
          await Task.Delay(SlowResponseDelayMs, req.HttpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
          return;
        }

        res.ContentType = MediaTypeNames.Text.Plain;
        await res.WriteAsync("slow-content");
      })
      .MapGet("slow-body.bin", async (req, res, data) =>
      {
        res.ContentType = MediaTypeNames.Application.Octet;
        res.ContentLength = (long)SlowBodyChunks * SlowBodyChunkSize;

        var chunk = new byte[SlowBodyChunkSize];
        try
        {
          for (var i = 0; i < SlowBodyChunks; i++)
          {
            await res.Body.WriteAsync(chunk, req.HttpContext.RequestAborted);
            await res.Body.FlushAsync(req.HttpContext.RequestAborted);
            await Task.Delay(SlowBodyChunkDelayMs, req.HttpContext.RequestAborted);
          }
        }
        catch (OperationCanceledException)
        {
          // The proxy gave up on the stall and dropped the connection; stop rather than surface a Kestrel
          // error from writing into it.
        }
      })
      .MapGet("v2/testimage/tags/list", (req, res, data) =>
      {
        res.ContentType = MediaTypeNames.Application.Json;
        return res.WriteAsync("{\"name\":\"testimage\",\"tags\":[\"24.04\"]}");
      })
      .MapGet("v2/_catalog", (req, res, data) =>
      {
        res.ContentType = MediaTypeNames.Application.Json;
        return res.WriteAsync("{\"repositories\":[\"testimage\"]}");
      })
      .MapGet("simple/testpkg/{*trailing}", (req, res, data) =>
      {
        Interlocked.Increment(ref SimpleIndexRequestCount);
        var accept = req.Headers.Accept.ToString();
        SimpleAcceptHeaders.Enqueue(accept);

        // PEP 691 negotiation: one URL, two representations chosen by Accept, each with an ETag of its
        // own - the shape VaryByAccept exists for. pypi.org is lenient, like Docker Hub on a manifest: it
        // falls back to HTML rather than 406, and that leniency is what makes a path-only cache key
        // dangerous here. The wrong representation is simply served, with no error anywhere to notice it.
        var wantsJson = accept.Contains(SimpleJsonMediaType, StringComparison.OrdinalIgnoreCase);
        res.ContentType = wantsJson ? SimpleJsonMediaType : MediaTypeNames.Text.Html;
        res.Headers.ETag = wantsJson ? "\"simple-json\"" : "\"simple-html\"";
        return res.WriteAsync(wantsJson
          ? "{\"name\":\"testpkg\",\"files\":[]}"
          : "<!DOCTYPE html><html><body></body></html>");
      })
      .MapGet("conditional-500.txt", (req, res, data) =>
      {
        if (Conditional500SendErrorOnce)
        {
          Conditional500SendErrorOnce = false;
          res.StatusCode = StatusCodes.Status500InternalServerError;
          return res.WriteAsync("Some Error");
        }

        return res.WriteAsync("ok");
      })
      .MapGet("500.jar", (req, res, data) =>
      {
        res.StatusCode = StatusCodes.Status500InternalServerError;
        return res.WriteAsync("Some Error");
      })
      .MapGet("401.jar", (req, res, data) =>
      {
        res.StatusCode = StatusCodes.Status401Unauthorized;
        return res.WriteAsync("Unauthorized");
      })
      .MapGet("402.jar", (req, res, data) =>
      {
        res.StatusCode = StatusCodes.Status402PaymentRequired;
        return res.WriteAsync("Payment Required");
      })
      .MapGet("403.jar", (req, res, data) =>
      {
        res.StatusCode = StatusCodes.Status403Forbidden;
        return res.WriteAsync("Forbidden");
      }).MapGet("wrong-content-length.jar", (req, res, data) =>
      {
        res.ContentLength = 1024;
        return res.WriteAsync("not too much");
      })
      .MapGet("revalidate.txt", (req, res, data) =>
      {
        Interlocked.Increment(ref RevalidateRequestCount);
        RevalidateIfNoneMatch = req.Headers.IfNoneMatch.ToString() is { Length: > 0 } ifNoneMatch ? ifNoneMatch : null;
        RevalidateIfModifiedSince = req.Headers.IfModifiedSince.ToString() is { Length: > 0 } since ? since : null;
        if (RevalidateLastModified is { } lastModified)
          res.Headers.LastModified = lastModified;
        if (RevalidateETag is { } etag)
          res.Headers.ETag = etag;
        switch (Revalidate)
        {
          case RevalidateBehavior.NotModified:
            res.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
          case RevalidateBehavior.NotFound:
            res.StatusCode = StatusCodes.Status404NotFound;
            return res.WriteAsync("gone");
          case RevalidateBehavior.ServerError:
            res.StatusCode = StatusCodes.Status500InternalServerError;
            return res.WriteAsync("boom");
          default:
            return res.WriteAsync(RevalidateContent);
        }
      })
      .MapGet("group/artifact/maven-metadata.xml", (req, res, data) =>
      {
        Interlocked.Increment(ref MavenMetadataRequestCount);
        // Conditional revalidation after the freshness window: report "unchanged".
        if (req.Headers.ContainsKey("If-Modified-Since") || req.Headers.ContainsKey("If-None-Match"))
        {
          res.StatusCode = StatusCodes.Status304NotModified;
          return Task.CompletedTask;
        }
        res.ContentType = MediaTypeNames.Text.Xml;
        return res.WriteAsync("<metadata><versioning/></metadata>");
      })
      .MapGet("group/artifact/1.0-SNAPSHOT/artifact-1.0-SNAPSHOT.jar", (req, res, data) =>
      {
        Interlocked.Increment(ref SnapshotRequestCount);
        return res.WriteAsync("snapshot-jar-content");
      })
      .MapGet("archetype-catalog.xml", (req, res, data) =>
      {
        res.ContentType = MediaTypeNames.Text.Xml;
        return res.WriteAsync("<archetype-catalog/>");
      })
      .MapGet("express", (req, res, data) =>
      {
        Interlocked.Increment(ref PackumentRequestCount);
        // Conditional revalidation after the freshness window: honour the scripted behavior so the
        // tests can drive REVALIDATED (304) or STALE (5xx). A first (unconditional) request returns
        // the full packument.
        if (req.Headers.ContainsKey("If-Modified-Since") || req.Headers.ContainsKey("If-None-Match"))
        {
          switch (Revalidate)
          {
            case RevalidateBehavior.NotModified:
              res.StatusCode = StatusCodes.Status304NotModified;
              return Task.CompletedTask;
            case RevalidateBehavior.ServerError:
              res.StatusCode = StatusCodes.Status500InternalServerError;
              return res.WriteAsync("boom");
          }
        }
        res.ContentType = MediaTypeNames.Application.Json;
        return res.WriteAsync("{\"name\":\"express\",\"dist-tags\":{\"latest\":\"1.0.0\"}}");
      })
      .MapGet("express/-/express-1.0.0.tgz", (req, res, data) =>
      {
        Interlocked.Increment(ref TarballRequestCount);
        return res.WriteAsync("npm-tarball-content");
      })
      // NuGet v3 flat container. The version list is conditional-aware, like the npm packument, so the
      // nuget tests can script a 304 (kept) or a 5xx (serve stale) after the freshness window.
      .MapGet("v3-flatcontainer/newtonsoft.json/index.json", (req, res, data) =>
      {
        Interlocked.Increment(ref NugetVersionListRequestCount);
        if (req.Headers.ContainsKey("If-Modified-Since") || req.Headers.ContainsKey("If-None-Match"))
        {
          switch (Revalidate)
          {
            case RevalidateBehavior.NotModified:
              res.StatusCode = StatusCodes.Status304NotModified;
              return Task.CompletedTask;
            case RevalidateBehavior.ServerError:
              res.StatusCode = StatusCodes.Status500InternalServerError;
              return res.WriteAsync("boom");
          }
        }
        res.ContentType = MediaTypeNames.Application.Json;
        return res.WriteAsync("{\"versions\":[\"13.0.3\"]}");
      })
      .MapGet("v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", (req, res, data) =>
      {
        Interlocked.Increment(ref NugetPackageRequestCount);
        return res.WriteAsync("nupkg-content");
      })
      .MapGet("a.jar", (req, res, data) => res.WriteAsync("a.jar"))
      // Declares Content-Length, which the routes around it do not: the byte counter reads a MISS's size
      // off the upstream head, so an upstream that declares none is counted only once its stored copy is
      // served back.
      .MapGet("sized.jar", (req, res, data) =>
      {
        res.ContentLength = "sized.jar"u8.Length;
        return res.WriteAsync("sized.jar");
      })
      // The entity's length and no body, as a real upstream answers a HEAD: the one length the byte
      // counter has to leave alone, since nothing is transferred and nothing but the head is cached.
      .MapVerb(HttpMethods.Head, "sized.jar", (req, res, data) =>
      {
        res.ContentLength = "sized.jar"u8.Length;
        return Task.CompletedTask;
      })
      .MapGet("chunked.bin", async (req, res, data) =>
      {
        // Flush after the first write so the response is sent chunked, with no Content-Length.
        await res.WriteAsync("chunk1");
        await res.Body.FlushAsync();
        await res.WriteAsync("chunk2");
      })
      .MapGet("gzipEncoding.txt", (req, res, data) =>
      {
        res.Headers.ContentEncoding = "gzip";
        var textContent = "my content string"u8;

        using var mso = new MemoryStream();

        using (var gs = new GZipStream(mso, CompressionMode.Compress))
          gs.Write(textContent);

        if (mso.TryGetBuffer(out var buffer))
          return res.Body.WriteAsync(buffer).AsTask();

        return Task.CompletedTask;
      })
      .MapVerb(HttpMethods.Head, "gzipEncoding.txt", (req, res, data) =>
      {
        res.Headers.ContentEncoding = "gzip";
        var textContent = "my content string"u8;

        using var mso = new MemoryStream();

        using (var gs = new GZipStream(mso, CompressionMode.Compress))
          gs.Write(textContent);

        if (mso.TryGetBuffer(out var buffer))
        {
          res.Headers.ContentLength = buffer.Count;
        }

        return Task.CompletedTask;
      })
      .MapGet("fakeBrEncoding.txt", (req, res, data) =>
      {
        res.Headers.ContentEncoding = "br";
        return res.WriteAsync("garbage");
      })
      .MapGet("fakeMultipleEncodings.txt", (req, res, data) =>
      {
        res.Headers.ContentEncoding = "deflate, gzip";
        return res.WriteAsync("garbage");
      })
      .MapGet("extensionless", (req, res, data) => res.WriteAsync("no-extension-content"))
      .MapVerb(HttpMethods.Head, "extensionless", (req, res, data) => Task.CompletedTask)
      .MapGet("@scope%2fpackage", (req, res, data) => res.WriteAsync("scoped-package-content"))
      .MapGet("name with spaces.jar", (req, res, data) => res.WriteAsync("zzz.jar"))
      .MapGet("name+with+plus.jar", (req, res, data) => res.WriteAsync("zzz.jar"))
      .MapGet("@username/package/-/package-3.1.2.tgz", (req, res, data) => res.WriteAsync("package-3.1.2.tgz"))
      .MapGet("a.jar/b.jar", (req, res, data) => res.WriteAsync("b.jar"))
      .MapGet("a.html", (req, res, data) =>
      {
        res.ContentType = MediaTypeNames.Text.Html;
        return res.WriteAsync("some html");
      })
      .MapGet("wrong-content-type.jar", (req, res, data) =>
      {
        res.ContentType = MediaTypeNames.Text.Html;
        return res.WriteAsync("some html");
      })
      .MapGet("artifact.pom", (req, res, data) => res.WriteAsync("<project/>"))
    );
  }

  public Task InitializeAsync() => myWebApp.StartAsync();

  public Task DisposeAsync() => myWebApp.StopAsync();
}
