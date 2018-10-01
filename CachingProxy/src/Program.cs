using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;

namespace JetBrains.CachingProxy
{
  public static class Program
  {
    public static void Main(string[] args)
    {
      WebHost.CreateDefaultBuilder<Startup>(args).Build().Run();
    }
  }
}
