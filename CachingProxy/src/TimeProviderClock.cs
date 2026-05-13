using System;
using Microsoft.Extensions.Internal;

namespace JetBrains.CachingProxy;

public class TimeProviderClock(TimeProvider timeProvider) : ISystemClock
{
  public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
