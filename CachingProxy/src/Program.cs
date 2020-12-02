using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace JetBrains.CachingProxy
{
  public static class Program
  {
    public static void Main(string[] args)
    {
      WebHost.CreateDefaultBuilder<Startup>(args)
        .ConfigureLogging(logging => logging.AddJsonConsole())
        .Build().Run();
    }
  }
}
