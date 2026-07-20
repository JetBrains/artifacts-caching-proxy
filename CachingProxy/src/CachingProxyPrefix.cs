namespace JetBrains.CachingProxy;

public record CachingProxyPrefix(string Prefix, CacheDuration? CacheDuration = null, string? Profile = null)
{
  public static implicit operator CachingProxyPrefix(string prefix) => new(prefix);

  public override string ToString()
  {
    var result = Prefix;
    if (CacheDuration != null) result += $" {CacheDuration}";
    if (Profile != null) result += $" [{Profile}]";
    return result;
  }
}
