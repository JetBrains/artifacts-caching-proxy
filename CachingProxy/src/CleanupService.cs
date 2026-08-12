using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public class CleanupService(TimeProvider timeProvider, CachingProxyConfig config, CachingProxyMetrics metrics, ILogger<CleanupService> logger) : BackgroundService
{
  private readonly Counter<long> myFilesDeletedCounter = metrics.Meter.CreateCounter<long>(
    "file_cleanup_deleted_files_total", "files", "Total number of files deleted by cleanup");

  private readonly Counter<long> myBytesDeletedCounter = metrics.Meter.CreateCounter<long>(
    "file_cleanup_deleted_bytes_total", "bytes", "Total bytes deleted by cleanup");

  private readonly Histogram<double> myRunDurationHistogram = metrics.Meter.CreateHistogram<double>(
    "file_cleanup_run_duration_seconds", "s", "Duration of a cleanup run in seconds");

  // What the cache itself holds, as opposed to what is free on its volume
  // (local_cache_disk_free_bytes). Both are needed on a shared host path: only the pair distinguishes
  // "the cache filled the disk" from "something else on the node did", and they imply different fixes.
  //
  // Measured from the enumeration below instead of a walk of its own, so it costs nothing and cannot
  // disagree with what cleanup saw. That ties its freshness to CleanupInterval - hourly in production -
  // which suits a number that only moves at cache scale.
  private long myCacheBytes = -1;
  private long myCacheFiles = -1;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (config.CleanupInterval is not { Length: > 0 } cleanupInterval)
    {
      logger.LogInformation("File cleanup interval is not configured.");
      return;
    }

    try
    {
      var cron = CronExpression.Parse(cleanupInterval, CronFormat.Standard);

      // Registered here rather than in a field initializer, which cannot reference the `this` the callbacks
      // read through. So the two instruments appear once the loop starts, not at construction - immaterial,
      // since they measure nothing until the first walk either way.
      metrics.Meter.CreateObservableGauge("local_cache_size_bytes",
        () => ObserveMeasured(Volatile.Read(ref myCacheBytes)),
        "bytes", "Bytes held by the local cache as of the last cleanup run");

      metrics.Meter.CreateObservableGauge("local_cache_files",
        () => ObserveMeasured(Volatile.Read(ref myCacheFiles)),
        "files", "Files held by the local cache as of the last cleanup run");

      while (!stoppingToken.IsCancellationRequested)
      {
        var from = timeProvider.GetUtcNow();
        if (cron.GetNextOccurrence(from, TimeZoneInfo.Utc) is not { } to)
        {
          logger.LogWarning("Cron expression {Cron} has no future occurrences; stopping cleanup loop.", cron);
          return;
        }
        await Task.Delay(to - from, timeProvider, stoppingToken);
        await CleanupOnceAsync(stoppingToken);
      }
    }
    catch (OperationCanceledException) { }
    catch (Exception e)
    {
      logger.LogError(e, "Unexpected error occurred while cleaning up.");
    }
  }

  /// <summary>
  /// Publishes a footprint field only once a cleanup run has filled it in. Nothing rather than zero before
  /// the first run: an empty cache is a real value that a fresh node genuinely reports, so a
  /// not-yet-measured one must not be able to forge it.
  /// </summary>
  private static IEnumerable<Measurement<long>> ObserveMeasured(long value)
  {
    if (value >= 0)
      yield return new Measurement<long>(value);
  }

  private Task CleanupOnceAsync(CancellationToken cancellationToken)
  {
    var localCachePath = config.LocalCachePath;
    if (string.IsNullOrWhiteSpace(localCachePath) || !Directory.Exists(localCachePath))
    {
      logger.LogWarning("Cleanup root path '{RootPath}' does not exist", localCachePath);
      return Task.CompletedTask;
    }

    var cutoffUtc = timeProvider.GetUtcNow() - config.CleanupPeriod;
    logger.LogInformation("File cleanup started. Deleted files older than {Cutoff}", cutoffUtc);

    var stopwatch = Stopwatch.StartNew();
    var deletedCount = 0;
    long deletedBytes = 0;
    var keptCount = 0;
    long keptBytes = 0;
    foreach (var filePath in Directory.EnumerateFiles(localCachePath, "*", SearchOption.AllDirectories))
    {
      cancellationToken.ThrowIfCancellationRequested();
      FileInfo fileInfo;
      try
      {
        fileInfo = new FileInfo(filePath);
      }
      catch (Exception ex)
      {
        logger.LogDebug(ex, "Failed to get FileInfo for {Path}", filePath);
        continue;
      }
      // A metadata file is exempt from the access-time cutoff: evicting it from under a busy artifact
      // would strand bytes with no media type to serve them as, forcing a refetch. It lives and dies
      // with its artifact instead, so the only one worth deleting here is an orphan.
      if (CacheFileProvider.IsMetadata(filePath))
      {
        if (!File.Exists(CacheFileProvider.GetMetadataOwnerPath(filePath)) && TryDelete(fileInfo))
          continue;
        CountKept(fileInfo);
        continue;
      }

      if (fileInfo.LastAccessTimeUtc < cutoffUtc && TryDelete(fileInfo))
        continue;

      CountKept(fileInfo);
    }

    var durationSeconds = stopwatch.Elapsed.TotalSeconds;

    // Update metrics
    myFilesDeletedCounter.Add(deletedCount);
    myBytesDeletedCounter.Add(deletedBytes);
    myRunDurationHistogram.Record(durationSeconds);
    Volatile.Write(ref myCacheBytes, keptBytes);
    Volatile.Write(ref myCacheFiles, keptCount);

    if (deletedCount > 0)
    {
      logger.LogInformation(
        "File cleanup completed. Deleted {Count} files, {Bytes} bytes (approx {Megabytes:F2} MB) older than {Cutoff} in {Duration:F2}s",
        deletedCount,
        deletedBytes,
        deletedBytes / (1024.0 * 1024.0),
        cutoffUtc,
        durationSeconds);
    }
    else
    {
      logger.LogDebug("File cleanup completed. No files older than {Cutoff}. Duration {Duration:F2}s",
        cutoffUtc, durationSeconds);
    }

    return Task.CompletedTask;

    bool TryDelete(FileInfo file)
    {
      try
      {
        var size = file.Length;
        file.Delete();
        deletedCount++;
        deletedBytes += size;
        return true;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Failed to delete {Path}", file.FullName);
        return false;
      }
    }

    // A file that failed to delete is still on disk, so it counts as kept. Length can throw where the time
    // fields only return a sentinel; a file racing us out of existence is normal, so treat it as gone.
    void CountKept(FileInfo file)
    {
      long size;
      try
      {
        size = file.Length;
      }
      catch (Exception ex)
      {
        logger.LogDebug(ex, "Failed to read length of {Path}", file.FullName);
        return;
      }

      keptCount++;
      keptBytes += size;
    }
  }
}
