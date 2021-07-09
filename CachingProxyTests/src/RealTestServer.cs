using System;
using System.Net;
using System.Net.Mime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
            .MapGet("name with spaces.jar", (req, res, data) => res.WriteAsync("zzz.jar"))
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
