using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace JetBrains.CachingProxy
{
  public class ProxyHttpClient
  {
    public HttpClient Client { get; }

    public ProxyHttpClient(HttpClient client)
    {
      Client = client;
    }

    // TODO. Move some client logic here
  }
}
