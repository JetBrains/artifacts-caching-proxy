using System;
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
  // that the proxy's freshness window is anchored to its own stored date rather than the upstream's.
  public volatile string? RevalidateLastModified;

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
        return next(context);
      })
      .UseRouter(router => router
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
        if (RevalidateLastModified is { } lastModified)
          res.Headers.LastModified = lastModified;
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
      .MapGet("a.jar", (req, res, data) => res.WriteAsync("a.jar"))
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
