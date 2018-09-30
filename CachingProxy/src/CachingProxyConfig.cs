using System.Collections.Generic;

namespace JetBrains.CachingProxy
{
  public class CachingProxyConfig
  {
    public List<string> Prefixes { get; set; } = new List<string>();
    public bool UseHttps { get; set; } = true;
    public string LocalCachePath { get; set; } = null;
    public string BlacklistUrlRegex { get; set; } = "^.*maven-metadata\\.xml$";
    public string RedirectToRemoteUrlsRegex { get; set; } = "^.*-SNAPSHOT.*$";
  }
}