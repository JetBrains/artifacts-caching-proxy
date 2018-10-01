using System.Net;

namespace JetBrains.CachingProxy
{
  public static class Utils
  {
    public static bool IsSuccessStatusCode(this HttpStatusCode code) =>
      code >= HttpStatusCode.OK && code <= (HttpStatusCode) 299;
  }
}
