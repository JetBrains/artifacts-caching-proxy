using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace JetBrains.CachingProxy.Tests
{
  public class RealTestServer : IDisposable
  {
    public static readonly int Port = 4455;
    public static readonly string Url = $"http://127.0.0.1:{Port}";

    private readonly IWebHost myWebHost;

    public RealTestServer()
    {
      myWebHost = new WebHostBuilder()
        .UseKestrel(kestrelServerOptions => { kestrelServerOptions.ListenLocalhost(Port); })
        .ConfigureServices(services => services
          .AddRouting())
        .Configure(app => app
          .UseRouter(router => router
            .MapGet("ok-after-first-retry", (req, res, data) => res.WriteAsync($"Hello, {data.Values["name"]}!"))
            .MapGet("500.jar", (req, res, data) =>
            {
              res.StatusCode = (int) HttpStatusCode.InternalServerError;
              return res.WriteAsync($"Some Error");
            })
            .MapGet("a.jar", (req, res, data) => res.WriteAsync($"a.jar"))
            .MapGet("a.jar/b.jar", (req, res, data) => res.WriteAsync($"b.jar"))
          ))
        .Start();
    }

    public void Dispose()
    {
      myWebHost.Dispose();
    }
  }
}
