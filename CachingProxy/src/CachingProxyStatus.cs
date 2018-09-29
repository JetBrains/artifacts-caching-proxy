namespace JetBrains.CachingProxy
{
  public enum CachingProxyStatus
  {
    HIT,
    NEGATIVE_HIT,
    MISS,
    NEGATIVE_MISS,
    BLACKLISTED,
    ALWAYS_REDIRECT,
    
    BAD_REQUEST,
    ERROR,
  }
}