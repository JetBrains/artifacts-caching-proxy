using System.IO;

namespace JetBrains.CachingProxy
{
  public class CachingProxyConfig
  {
    public string[] Prefixes { get; init; } = [];
    public string[] ContentTypeValidationPrefixes { get; init; } = [];
    public string LocalCachePath { get; init; } = Path.Combine(Path.GetTempPath(), "artifacts-caching-proxy");
    public string? BlacklistUrlRegex { get; init; }
    public long MinimumFreeDiskSpaceMb { get; init; } = 2048;
    public long RequestTimeoutSec { get; init; } = 20;
    public string RedirectToRemoteUrlsRegex { get; init; } = "^(.*-SNAPSHOT.*|.*maven-metadata\\.xml)$";

    public override string ToString()
    {
      return $"{nameof(Prefixes)}: {string.Join(", ", Prefixes)},\n" +
             $"{nameof(ContentTypeValidationPrefixes)}: {string.Join(", ", ContentTypeValidationPrefixes)},\n" +
             $"{nameof(LocalCachePath)}: {LocalCachePath},\n" +
             $"{nameof(BlacklistUrlRegex)}: {BlacklistUrlRegex},\n" +
             $"{nameof(MinimumFreeDiskSpaceMb)}: {MinimumFreeDiskSpaceMb},\n" +
             $"{nameof(RequestTimeoutSec)}: {RequestTimeoutSec},\n" +
             $"{nameof(RedirectToRemoteUrlsRegex)}: {RedirectToRemoteUrlsRegex}";
    }
  }
}
