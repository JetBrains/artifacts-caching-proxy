using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
      if (env.IsDevelopment())
        app.UseDeveloperExceptionPage();

      app.UseMiddleware<CachingProxy>();
    }

    public static void ConfigureOurServices(IServiceCollection services)
    {
      services.AddHttpClient<ProxyHttpClient>((provider, client) =>
        {
          var config = provider.GetRequiredService<IOptions<CachingProxyConfig>>().Value;
          client.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSec);
        })
        .AddTransientHttpErrorPolicy(builder => builder.WaitAndRetryAsync(new[]
        {
          TimeSpan.FromSeconds(1),
          TimeSpan.FromSeconds(2),
          TimeSpan.FromSeconds(3),
          TimeSpan.FromSeconds(5)
        }));
    }
  }
}
