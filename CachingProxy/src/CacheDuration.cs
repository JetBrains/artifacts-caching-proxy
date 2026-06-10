using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace JetBrains.CachingProxy;

public class CacheDuration : Dictionary<HttpStatusCode, TimeSpan>
{
  public static readonly CacheDuration Default = new()
  {
    // Clear reply from a server
    [HttpStatusCode.OK] = TimeSpan.FromMinutes(5),
    [HttpStatusCode.NotFound] = TimeSpan.FromMinutes(5),
  };

  public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(1);
  public static readonly TimeSpan CacheOffsetDuration = TimeSpan.FromSeconds(5);

  public override string ToString() =>
    $"{{ {string.Join(", ", this.Select(kvp => $"{kvp.Key}={kvp.Value}"))} }}";
}
