using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace JetBrains.CachingProxy.Tests
{
  public class RealTestServer : IDisposable
  {
    private const int Port = 4455;
    public static readonly string Url = $"http://127.0.0.1:{Port}";

    private readonly IWebHost myWebHost;
    public volatile bool Conditional500SendErrorOnce;

    public RealTestServer()
    {
      myWebHost = new WebHostBuilder()
        .UseKestrel(kestrelServerOptions => { kestrelServerOptions.ListenLocalhost(Port); })
        .ConfigureServices(services => services
          .AddRouting())
        .Configure(app => app
          .UseRouter(router => router
            .MapGet("conditional-500.txt", (req, res, data) =>
            {
              if (Conditional500SendErrorOnce)
              {
                Conditional500SendErrorOnce = false;
                res.StatusCode = (int) HttpStatusCode.InternalServerError;
                return res.WriteAsync("Some Error");
              }

              return res.WriteAsync("ok");
            })
            .MapGet("500.jar", (req, res, data) =>
            {
              res.StatusCode = (int) HttpStatusCode.InternalServerError;
              return res.WriteAsync("Some Error");
            }).MapGet("wrong-content-length.jar", (req, res, data) =>
            {
              res.ContentLength = 1024;
              return res.WriteAsync("not too much");
            })
            .MapGet("a.jar", (req, res, data) => res.WriteAsync("a.jar"))
            .MapGet("gzipEncoding.txt", (req, res, data) =>
            {
              res.Headers[HeaderNames.ContentEncoding] = "gzip";
              var textContent = Encoding.UTF8.GetBytes("my content string");

              using var mso = new MemoryStream();

              using (var gs = new GZipStream(mso, CompressionMode.Compress))
                new MemoryStream(textContent).CopyTo(gs);

              var buffer = mso.ToArray();

              return res.Body.WriteAsync(buffer, 0, buffer.Length);
            })
            .MapVerb("HEAD", "gzipEncoding.txt", (req, res, data) =>
            {
              res.Headers[HeaderNames.ContentEncoding] = "gzip";
              var textContent = Encoding.UTF8.GetBytes("my content string");

              using var mso = new MemoryStream();

              using (var gs = new GZipStream(mso, CompressionMode.Compress))
                new MemoryStream(textContent).CopyTo(gs);

              var buffer = mso.ToArray();

              res.Headers["Content-Length"] = buffer.Length.ToString();
              return Task.CompletedTask;
            })
            .MapGet("fakeBrEncoding.txt", (req, res, data) =>
            {
              res.Headers[HeaderNames.ContentEncoding] = "br";
              return res.WriteAsync("garbage");
            })
            .MapGet("fakeMultipleEncodings.txt", (req, res, data) =>
            {
              res.Headers[HeaderNames.ContentEncoding] = "deflate, gzip";
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
          ))
        .Start();
    }

    public void Dispose()
    {
      myWebHost.Dispose();
    }
  }
}
