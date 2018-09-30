using System.Collections.Generic;
using System.IO;

namespace JetBrains.CachingProxy
{
  public class CachingProxyConfig
  {
    public List<string> Prefixes { get; set; } = new List<string>();
    public string LocalCachePath { get; set; } = Path.Combine(Path.GetTempPath(), "artifacts-caching-proxy");
    public string BlacklistUrlRegex { get; set; } = "^.*maven-metadata\\.xml$";
    public string RedirectToRemoteUrlsRegex { get; set; } = "^.*-SNAPSHOT.*$";
  }
}