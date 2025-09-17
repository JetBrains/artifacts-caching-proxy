using System.Net;

namespace JetBrains.CachingProxy
{
  public static class Utils
  {
    public static bool IsSuccessStatusCode(this HttpStatusCode code) =>
      code is >= HttpStatusCode.OK and <= (HttpStatusCode) 299;
  }
}
