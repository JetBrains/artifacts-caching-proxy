using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace JetBrains.CachingProxy;

public class CacheDuration : Dictionary<HttpStatusCode, TimeSpan>
{
  public CacheDuration()
  {
    // Clear reply from a server
    this[HttpStatusCode.OK] = TimeSpan.FromMinutes(5);
    this[HttpStatusCode.NotFound] = TimeSpan.FromMinutes(5);
  }

  public CacheDuration(IDictionary<HttpStatusCode, TimeSpan> baseline) : base(baseline) { }

  public CacheDuration Union(CacheDuration? other)
  {
    if (other == null) return this;

    var union = new CacheDuration(this);
    foreach (var (key, value) in other)
    {
      union[key] = value;
    }
    return union;
  }

  private static readonly TimeSpan ourDefaultDuration = TimeSpan.FromMinutes(1);

  public TimeSpan GetDuration(HttpStatusCode statusCode) =>
    TryGetValue(statusCode, out var value) ? value : ourDefaultDuration;

  public override string ToString() =>
    $"{{ {string.Join(", ", this.Select(kvp => $"{kvp.Key}={kvp.Value}"))} }}";
}
