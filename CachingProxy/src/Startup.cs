using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace JetBrains.CachingProxy
{
  public class Startup
  {
    public void Configure(IApplicationBuilder app, IHostingEnvironment env)
    {
      if (env.IsDevelopment())
        app.UseDeveloperExceptionPage();

      app.UseMiddleware<CachingProxy>();
    }
  }
}