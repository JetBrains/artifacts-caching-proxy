using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

/// <summary>MRI-5689: per-prefix retention - its configuration, the disk metadata field and the disk cleanup.</summary>
public class RetentionPolicyTest : IDisposable
{
  private readonly List<string> myTempDirectories = [];

  [Theory]
  [InlineData("1d", 1)]
  [InlineData("3d", 3)]
  [InlineData("30d", 30)]
  [InlineData("180d", 180)]
  [InlineData("1y", 365)]
  [InlineData(" 3D ", 3)]
  public void Parses_The_Predefined_Values(string value, int days)
  {
    var policy = RetentionPolicy.Parse(value);

    Assert.NotNull(policy);
    Assert.Equal(TimeSpan.FromDays(days), policy.Period);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData(" ")]
  public void No_Value_Is_No_Policy(string? value) => Assert.Null(RetentionPolicy.Parse(value));

  [Theory]
  [InlineData("2d")]
  [InlineData("7d")]
  [InlineData("365d")]
  [InlineData("forever")]
  public void A_Prefix_With_An_Unknown_Retention_Fails_Startup(string retention)
  {
    // No bucket lifecycle rule would match the tag, so its objects would never expire.
    var config = new CachingProxyConfig { Prefixes = [new CachingProxyPrefix("/a=repo.example.com/maven", Retention: retention)] };

    var error = Assert.Throws<ArgumentException>(() => new RemoteServers(config, NullLogger<RemoteServers>.Instance));

    Assert.Contains(retention, error.Message);
    Assert.Contains("/a=repo.example.com/maven", error.Message);
  }

  [Fact]
  public void Retention_Binds_Per_Prefix_From_Configuration()
  {
    const string json = """
      {
        "Prefixes": [
          { "Prefix": "/tc=teamcity.example.com/artifacts", "Retention": "3d" },
          { "Prefix": "/maven=repo.example.com/maven" }
        ]
      }
      """;
    var config = new ConfigurationBuilder()
      .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
      .Build()
      .Get<CachingProxyConfig>()!;

    var servers = new RemoteServers(config, NullLogger<RemoteServers>.Instance).Endpoints
      .Select(static e => e.Metadata.GetMetadata<RemoteServers.RemoteServer>()!)
      .ToDictionary(static s => s.Prefix.Value!);

    Assert.Same(RetentionPolicy.ThreeDays, servers["/tc"].Retention);
    Assert.Null(servers["/maven"].Retention);
  }

  [Fact]
  public void Metadata_Round_Trips_The_Retention()
  {
    var original = new CacheEntryMetadata(new DateTime(2026, 10, 30, 7, 0, 0, DateTimeKind.Utc), "application/zip",
      Retention: RetentionPolicy.OneDay);

    var text = original.Format();

    Assert.Contains("Retention: 1d\n", text);
    Assert.Equal(original, CacheEntryMetadata.TryParse(text));
  }

  [Theory]
  [InlineData("StoredAt: 2026-10-30T07:00:00.0000000Z\n")]                    // written before retention existed
  [InlineData("StoredAt: 2026-10-30T07:00:00.0000000Z\nRetention: 2w\n")]     // a value this version does not know
  public void Metadata_Without_A_Known_Retention_Has_None(string text)
  {
    var parsed = CacheEntryMetadata.TryParse(text);

    Assert.NotNull(parsed);
    Assert.Null(parsed.Retention);
  }

  [Fact]
  public async Task Cleanup_Honours_The_Metadata_Retention_And_Leaves_Others_On_The_Cleanup_Period()
  {
    var cachePath = NewTempDirectory();
    var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var now = timeProvider.GetUtcNow().UtcDateTime;

    // CleanupPeriod is 7 days below.
    var shortPolicyExpired = Store(cachePath, "short-expired.zip", now - TimeSpan.FromDays(5), RetentionPolicy.ThreeDays);
    var shortPolicyKept = Store(cachePath, "short-kept.zip", now - TimeSpan.FromDays(2), RetentionPolicy.ThreeDays);
    var longPolicyKept = Store(cachePath, "long-kept.jar", now - TimeSpan.FromDays(30), RetentionPolicy.OneYear);
    var noPolicyKept = Store(cachePath, "default-kept.jar", now - TimeSpan.FromDays(5), retention: null);
    var noPolicyExpired = Store(cachePath, "default-expired.jar", now - TimeSpan.FromDays(30), retention: null);
    var noMetadataExpired = Path.Combine(cachePath, "no-metadata.jar");
    File.WriteAllBytes(noMetadataExpired, new byte[10]);
    File.SetLastAccessTimeUtc(noMetadataExpired, now - TimeSpan.FromDays(30));

    var config = new CachingProxyConfig
    {
      LocalCachePath = cachePath,
      CleanupInterval = "* * * * *",
      CleanupPeriod = TimeSpan.FromDays(7),
    };
    var metrics = new ServiceCollection().AddMetrics().AddSingleton(config).AddSingleton<CachingProxyMetrics>()
      .BuildServiceProvider().GetRequiredService<CachingProxyMetrics>();
    using var cleanup = new CleanupService(timeProvider, config, metrics, NullLogger<CleanupService>.Instance);
    await cleanup.StartAsync(CancellationToken.None);
    try
    {
      // The loop waits on a fake-clock timer: advance whole cron periods until the walk has run.
      for (var attempt = 0; File.Exists(noMetadataExpired) && attempt < 10; attempt++)
      {
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 20; i++) await Task.Delay(10);
      }
    }
    finally
    {
      await cleanup.StopAsync(CancellationToken.None);
    }

    Assert.False(File.Exists(noMetadataExpired));
    Assert.False(File.Exists(shortPolicyExpired));
    Assert.False(File.Exists(noPolicyExpired));
    Assert.True(File.Exists(shortPolicyKept));
    Assert.True(File.Exists(longPolicyKept));
    Assert.True(File.Exists(noPolicyKept));
    // The layout is unchanged: the policy lives in the companion metadata file next to the kept file.
    Assert.True(File.Exists(CacheFileProvider.GetMetadataPath(longPolicyKept)));
  }

  private static string Store(string cachePath, string name, DateTime lastAccessUtc, RetentionPolicy? retention)
  {
    var path = Path.Combine(cachePath, name);
    File.WriteAllBytes(path, new byte[10]);
    File.WriteAllText(CacheFileProvider.GetMetadataPath(path),
      new CacheEntryMetadata(lastAccessUtc, Retention: retention).Format());
    File.SetLastAccessTimeUtc(path, lastAccessUtc);
    return path;
  }

  private string NewTempDirectory()
  {
    var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(path);
    myTempDirectories.Add(path);
    return path;
  }

  public void Dispose()
  {
    foreach (var path in myTempDirectories)
    {
      try { Directory.Delete(path, recursive: true); }
      catch (IOException) { }
    }
  }
}
