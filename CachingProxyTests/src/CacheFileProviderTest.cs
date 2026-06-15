using System;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class CacheFileProviderTest
{
  // All three config prefixes in the original test (/a, /b=a, /c/d=a/) resolve to the same upstream
  // https://a/, so the mangled cache location depends only on (RemoteUri, remainingPath). Prefix and
  // alias parsing now lives in RemoteServers and is covered by RemoteServersTest.
  private static readonly RemoteServers.RemoteServer ourServer = new("/a", new Uri("https://a/"), new CacheDuration());

  [Fact]
  public void ManglePath1()
  {
    Assert.Equal(
      "d9/6d/d96d0bd13935d4ab082c410dea64c70bf2f926b75f3b487ac18c0e290ee8ac3a.jar",
      CacheFileProvider.GetFutureCacheFileLocation(ourServer, "a.jar"));
  }

  [Fact]
  public void ManglePath2()
  {
    // A trailing slash makes the upstream key "a/a.jar/", which has no file extension.
    Assert.Equal(
      "14/40/1440b34e1707076ba9c32fd06c18405254883be42d14cd240f237eaa3eb5960c",
      CacheFileProvider.GetFutureCacheFileLocation(ourServer, "a.jar/"));
  }

  [Fact]
  public void ManglePath_LeadingSlashIsIgnored()
  {
    // The route catch-all value has no leading slash, but a leading slash must hash identically.
    Assert.Equal(
      CacheFileProvider.GetFutureCacheFileLocation(ourServer, "a.jar"),
      CacheFileProvider.GetFutureCacheFileLocation(ourServer, "/a.jar"));
  }

  [Fact]
  public void ManglePath_IsCaseSensitive()
  {
    // Upstreams are case-sensitive, so paths differing only in case must map to distinct cache files.
    Assert.NotEqual(
      CacheFileProvider.GetFutureCacheFileLocation(ourServer, "Foo.jar"),
      CacheFileProvider.GetFutureCacheFileLocation(ourServer, "foo.jar"));
  }

  [Fact]
  public void ManglePath_GzipVariantAppendsSuffix()
  {
    // The gzip variant differs from the plain one only by a suffix appended after the hash.
    var plain = CacheFileProvider.GetFutureCacheFileLocation(ourServer, "a.jar");
    var gzip = CacheFileProvider.GetFutureCacheFileLocation(ourServer, "a.jar", new StringSegment("gzip"));
    Assert.Equal(plain + "-gzip-Ege4dHyCEA7IM", gzip);
  }
}
