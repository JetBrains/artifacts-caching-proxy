using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JetBrains.CachingProxy;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public class CleanupService(TimeProvider timeProvider, IOptions<CachingProxyConfig> options, CachingProxyMetrics metrics, ILogger<CleanupService> logger) : BackgroundService
{
  private readonly Counter<long> myFilesDeletedCounter = metrics.Meter.CreateCounter<long>(
    "file_cleanup_deleted_files_total", "files", "Total number of files deleted by cleanup");

  private readonly Counter<long> myBytesDeletedCounter = metrics.Meter.CreateCounter<long>(
    "file_cleanup_deleted_bytes_total", "bytes", "Total bytes deleted by cleanup");

  private readonly Histogram<double> myRunDurationHistogram = metrics.Meter.CreateHistogram<double>(
    "file_cleanup_run_duration_seconds", "s", "Duration of a cleanup run in seconds");

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (options.Value.CleanupInterval is not { Length: > 0 } cleanupInterval)
    {
      logger.LogInformation("File cleanup interval is not configured.");
      return;
    }

    try
    {
      var cron = CronExpression.Parse(cleanupInterval, CronFormat.Standard);
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
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception e)
    {
      logger.LogError(e, "Unexpected error occurred while cleaning up.");
    }
  }

  private Task CleanupOnceAsync(CancellationToken cancellationToken)
  {
    var localCachePath = options.Value.LocalCachePath;
    if (string.IsNullOrWhiteSpace(localCachePath) || !Directory.Exists(localCachePath))
    {
      logger.LogWarning("Cleanup root path '{RootPath}' does not exist", localCachePath);
      return Task.CompletedTask;
    }

    var cutoffUtc = timeProvider.GetUtcNow() - options.Value.CleanupPeriod;
    logger.LogInformation("File cleanup started. Deleted files older than {Cutoff}", cutoffUtc);

    var stopwatch = Stopwatch.StartNew();
    var deletedCount = 0;
    long deletedBytes = 0;
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
      if (fileInfo.LastAccessTimeUtc >= cutoffUtc)
        continue;

      try
      {
        var size = fileInfo.Length;
        fileInfo.Delete();
        deletedCount++;
        deletedBytes += size;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Failed to delete {Path}", filePath);
      }
    }

    var durationSeconds = stopwatch.Elapsed.TotalSeconds;

    // Update metrics
    myFilesDeletedCounter.Add(deletedCount);
    myBytesDeletedCounter.Add(deletedBytes);
    myRunDurationHistogram.Record(durationSeconds);

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
  }
}
