using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace JetBrains.CachingProxy;

public partial class CachingProxy
{
  public static class Event
  {
    public static readonly EventId NotEnoughFreeDiskSpace = new(1, nameof(NotEnoughFreeDiskSpace));
    public static readonly EventId MultipleContentTypes = new(2, nameof(MultipleContentTypes));
    public static readonly EventId NotSupportedContentType = new(3, nameof(NotSupportedContentType));
    public static readonly EventId NotAllowedContentType = new(4, nameof(NotAllowedContentType));
    public static readonly EventId NotMatchedContentLength = new(5, nameof(NotMatchedContentLength));
    public static readonly EventId Timeout = new(6, nameof(Timeout));

    private static readonly ConcurrentDictionary<HttpStatusCode, EventId> ourNegativeHitEventIDs = new();
    public static EventId NegativeMiss(HttpStatusCode httpStatusCode) =>
      ourNegativeHitEventIDs.GetOrAdd(httpStatusCode, static code => new EventId(1000 + (int)code, code.ToString()));
  }
}
