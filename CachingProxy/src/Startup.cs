using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace JetBrains.CachingProxy
{
  public class Startup
  {
    private readonly IConfiguration myConfig;

    public Startup(IConfiguration config)
    {
      myConfig = config;
    }

    public void ConfigureServices(IServiceCollection services)
    {
      services.Configure<CachingProxyConfig>(myConfig);
      ConfigureOurServices(services);
    }

    public void Configure(IApplicationBuilder app, IHostingEnvironment env)
    {
      if (env.IsDevelopment())
        app.UseDeveloperExceptionPage();

      app.UseMiddleware<CachingProxy>();
    }

    public static void ConfigureOurServices(IServiceCollection services)
    {
      services.AddHttpClient<ProxyHttpClient>(client => { client.Timeout = TimeSpan.FromSeconds(5); })
        .AddTransientHttpErrorPolicy(builder => builder.WaitAndRetryAsync(new[]
        {
          TimeSpan.FromSeconds(1)
        }));
    }
  }
}
