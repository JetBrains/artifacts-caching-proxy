using System.IO;

namespace JetBrains.CachingProxy
{
  public class CachingProxyConfig
  {
    public string[] Prefixes { get; set; } = new string[0];
    public string LocalCachePath { get; set; } = Path.Combine(Path.GetTempPath(), "artifacts-caching-proxy");
    public string BlacklistUrlRegex { get; set; } = null;
    public string RedirectToRemoteUrlsRegex { get; set; } = "^(.*-SNAPSHOT.*|.*maven-metadata\\.xml)$";

    public override string ToString()
    {
      return $"{nameof(Prefixes)}: {string.Join(", ", Prefixes)},\n" +
             $"{nameof(LocalCachePath)}: {LocalCachePath},\n" +
             $"{nameof(BlacklistUrlRegex)}: {BlacklistUrlRegex},\n" +
             $"{nameof(RedirectToRemoteUrlsRegex)}: {RedirectToRemoteUrlsRegex}";
    }
  }
}
