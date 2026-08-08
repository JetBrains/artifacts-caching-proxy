using System;
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
      ourServer.GetUpstreamUri("a.jar").GetFutureCacheFileLocation());
  }

  [Fact]
  public void ManglePath2()
  {
    // A trailing slash makes the upstream key "a/a.jar/", which has no file extension.
    Assert.Equal(
      "14/40/1440b34e1707076ba9c32fd06c18405254883be42d14cd240f237eaa3eb5960c",
      ourServer.GetUpstreamUri("a.jar/").GetFutureCacheFileLocation());
  }

  [Fact]
  public void ManglePath_LeadingSlashIsIgnored()
  {
    // The route catch-all value has no leading slash, but a leading slash must hash identically.
    Assert.Equal(
      ourServer.GetUpstreamUri("a.jar").GetFutureCacheFileLocation(),
      ourServer.GetUpstreamUri("/a.jar").GetFutureCacheFileLocation());
  }

  [Fact]
  public void ManglePath_IsCaseSensitive()
  {
    // Upstreams are case-sensitive, so paths differing only in case must map to distinct cache files.
    Assert.NotEqual(
      ourServer.GetUpstreamUri("Foo.jar").GetFutureCacheFileLocation(),
      ourServer.GetUpstreamUri("foo.jar").GetFutureCacheFileLocation());
  }

  [Fact]
  public void ManglePath_GzipVariantAppendsSuffix()
  {
    // The gzip variant differs from the plain one only by a suffix appended after the hash.
    var plain = ourServer.GetUpstreamUri("a.jar").GetFutureCacheFileLocation();
    var gzip = ourServer.GetUpstreamUri("a.jar").GetFutureCacheFileLocation("gzip");
    Assert.Equal(plain + "-gzip-Ege4dHyCEA7IM", gzip);
  }

  [Fact]
  public void Metadata_Path_Round_Trips_To_Its_Owner()
  {
    var cacheFile = ourServer.GetUpstreamUri("a.jar").GetFutureCacheFileLocation();
    var metadata = CacheFileProvider.GetMetadataPath(cacheFile);

    Assert.True(CacheFileProvider.IsMetadata(metadata));
    Assert.False(CacheFileProvider.IsMetadata(cacheFile));
    Assert.Equal(cacheFile, CacheFileProvider.GetMetadataOwnerPath(metadata));
  }

  [Fact]
  public void Metadata_Round_Trips_Every_Value()
  {
    var stored = new DateTime(2026, 8, 8, 10, 20, 30, DateTimeKind.Utc);
    var original = new CacheEntryMetadata(
      stored,
      "application/vnd.oci.image.index.v1+json",
      "\"a1b2c3\"",
      new DateTimeOffset(2026, 8, 7, 1, 2, 3, TimeSpan.Zero),
      "sha256:" + new string('a', 64));

    var parsed = CacheEntryMetadata.TryParse(original.Format());

    Assert.Equal(original, parsed);
  }

  [Fact]
  public void Metadata_Round_Trips_With_Only_The_Stored_Date()
  {
    // An upstream may send none of the head values we keep; the entry is still valid and still has a
    // freshness window to anchor.
    var original = new CacheEntryMetadata(new DateTime(2026, 8, 8, 10, 20, 30, DateTimeKind.Utc));

    var parsed = CacheEntryMetadata.TryParse(original.Format());

    Assert.Equal(original, parsed);
    Assert.Null(parsed!.ContentType);
    Assert.Null(parsed.ETag);
    Assert.Null(parsed.LastModified);
    Assert.Null(parsed.Digest);
  }

  [Theory]
  [InlineData("")]
  [InlineData("\n")]
  [InlineData("Content-Type: application/json\n")]     // head values but no stored date
  [InlineData("StoredAt: not-a-date\n")]
  [InlineData("2026-08-08T10:20:30.0000000Z\napplication/json")] // the shape an older version wrote
  public void Metadata_Without_A_Stored_Date_Is_Not_Metadata(string text)
  {
    // Null makes the caller treat the entry as absent: one refetch, which rewrites the companion.
    Assert.Null(CacheEntryMetadata.TryParse(text));
  }

  [Fact]
  public void Metadata_Ignores_Fields_It_Does_Not_Know()
  {
    // Forward compatibility: a companion written by a version that keeps more parses as far as the two
    // agree, rather than being discarded whole.
    var parsed = CacheEntryMetadata.TryParse(
      "StoredAt: 2026-08-08T10:20:30.0000000Z\nContent-Type: application/json\nX-Something-New: 42\n");

    Assert.NotNull(parsed);
    Assert.Equal("application/json", parsed!.ContentType);
  }
}
