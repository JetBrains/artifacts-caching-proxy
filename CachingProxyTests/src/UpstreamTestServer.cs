using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// ReSharper disable once ClassNeverInstantiated.Global
public class UpstreamTestServer : IAsyncLifetime
{
  public string Url => myWebApp
    .Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>().Addresses.Single();

  private readonly WebApplication myWebApp;
  public volatile bool Conditional500SendErrorOnce;

  public UpstreamTestServer()
  {
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
    myWebApp = builder.Build();

    myWebApp.UseRouter(router => router
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
      }).MapGet("wrong-content-length.jar", (req, res, data) =>
      {
        res.ContentLength = 1024;
        return res.WriteAsync("not too much");
      })
      .MapGet("a.jar", (req, res, data) => res.WriteAsync("a.jar"))
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
    );
  }

  public Task InitializeAsync() => myWebApp.StartAsync();

  public Task DisposeAsync() => myWebApp.StopAsync();
}
