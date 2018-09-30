using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    }

    public void Configure(IApplicationBuilder app, IHostingEnvironment env)
    {
      if (env.IsDevelopment())
        app.UseDeveloperExceptionPage();
      
      app.UseMiddleware<CachingProxy>();
    }
  }
}