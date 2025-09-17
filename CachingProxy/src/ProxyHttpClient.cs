using System.Net.Http;

namespace JetBrains.CachingProxy
{
  public class ProxyHttpClient(HttpClient client)
  {
    public HttpClient Client { get; } = client;

    // TODO. Move some client logic here
  }
}
