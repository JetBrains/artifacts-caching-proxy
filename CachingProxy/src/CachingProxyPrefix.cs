namespace JetBrains.CachingProxy;

public record CachingProxyPrefix(string Prefix, CacheDuration? CacheDuration = null)
{
  public static implicit operator CachingProxyPrefix(string prefix) => new(prefix);

  public override string ToString() =>
    CacheDuration == null ? Prefix : $"{Prefix} {CacheDuration}";
}
