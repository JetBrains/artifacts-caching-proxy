using System;

namespace JetBrains.CachingProxy.Tests;

// ReSharper disable once ClassNeverInstantiated.Global
public class CachingProxyFixture : IDisposable
{
  public readonly RealTestServer RealTestServer = new();

  public void Dispose() => RealTestServer.Dispose();
}
