using System;
using System.Collections.Generic;
using System.Linq;

namespace JetBrains.CachingProxy;

/// <summary>
/// How long a prefix's stored copies are kept (MRI-5689), set per prefix by <see cref="CachingProxyPrefix.Retention"/>.
/// Only a fixed set, because on S3 every value is a bucket lifecycle rule filtered on the
/// <see cref="TagKey"/> object tag: a value without a matching rule would be tagged and never expire.
/// <para>A prefix without one keeps what it has today: on disk the global
/// <see cref="CachingProxyConfig.CleanupPeriod"/>, on S3 no tag at all, so the bucket's own default rule
/// (<see cref="S3Default"/>) covers it just like every object stored before the tag existed.</para>
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
  /// The bucket's untagged default expiration, which applies to every object without a
  /// <see cref="TagKey"/> tag - including all objects stored before retention existed.
  /// </summary>
  public static RetentionPolicy S3Default => OneYear;

  private RetentionPolicy(string name, TimeSpan period)
  {
    Name = name;
    Period = period;
  }

  /// <summary>The configured spelling, which is also the S3 tag value and the disk metadata value.</summary>
  public string Name { get; }

  public TimeSpan Period { get; }

  /// <summary>Null for no value (no policy); throws for a value outside the fixed set, so startup fails.</summary>
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
