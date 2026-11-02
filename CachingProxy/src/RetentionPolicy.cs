using System;
using System.Collections.Generic;
using System.Linq;

namespace JetBrains.CachingProxy;

/// <summary>
/// Per-prefix retention (MRI-5689). A fixed set: each value needs a matching S3 lifecycle rule on the
/// <see cref="TagKey"/> tag, or tagged objects fall back to <see cref="S3Default"/>.
/// </summary>
public sealed record RetentionPolicy
{
  /// <summary>The S3 object tag key the bucket lifecycle rules filter on.</summary>
  public const string TagKey = "retention";

  public static readonly RetentionPolicy OneDay = new("1d", TimeSpan.FromDays(1));
  public static readonly RetentionPolicy ThreeDays = new("3d", TimeSpan.FromDays(3));
  public static readonly RetentionPolicy ThirtyDays = new("30d", TimeSpan.FromDays(30));
  public static readonly RetentionPolicy HalfYear = new("180d", TimeSpan.FromDays(180));
  public static readonly RetentionPolicy OneYear = new("1y", TimeSpan.FromDays(365));

  public static IReadOnlyList<RetentionPolicy> All { get; } = [OneDay, ThreeDays, ThirtyDays, HalfYear, OneYear];

  /// <summary>
  /// The bucket's catch-all expiration, which applies to tagged objects too: S3 takes the shortest
  /// matching rule. Must match `data_bucket_default_expiration_days` in Cache-Redirector.
  /// </summary>
  public static RetentionPolicy S3Default => OneYear;

  /// <summary>
  /// The earliest moment S3 lifecycle may delete an object created at <paramref name="createdAt"/>: the
  /// shorter of <paramref name="retention"/> (null: untagged) and <see cref="S3Default"/>, rounded up to
  /// midnight UTC as S3 does. The removal itself is asynchronous and may come later, never earlier.
  /// https://docs.aws.amazon.com/AmazonS3/latest/userguide/intro-lifecycle-rules.html
  /// </summary>
  public static DateTimeOffset S3ExpiresAt(DateTimeOffset createdAt, RetentionPolicy? retention)
  {
    var period = retention != null && retention.Period < S3Default.Period ? retention.Period : S3Default.Period;
    var due = (createdAt + period).ToUniversalTime();
    var midnight = new DateTimeOffset(due.UtcDateTime.Date, TimeSpan.Zero);
    return midnight == due ? midnight : midnight.AddDays(1);
  }

  private RetentionPolicy(string name, TimeSpan period)
  {
    Name = name;
    Period = period;
  }

  /// <summary>Config value, S3 tag value and disk metadata value.</summary>
  public string Name { get; }

  public TimeSpan Period { get; }

  /// <summary>Null for no value; throws for an unknown one.</summary>
  public static RetentionPolicy? Parse(string? value)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    return TryParse(value, out var policy)
      ? policy
      : throw new ArgumentException(
        $"Unknown retention '{value}'. Allowed values: {string.Join(", ", All.Select(static p => p.Name))}");
  }

  public static bool TryParse(string? value, out RetentionPolicy? policy)
  {
    var trimmed = value?.Trim();
    policy = All.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    return policy != null;
  }

  public override string ToString() => Name;
}
