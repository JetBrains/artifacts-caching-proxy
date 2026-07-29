using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace JetBrains.CachingProxy;

public class CachingProxyMetrics
{
  public static readonly string MeterName = typeof(CachingProxyMetrics).Namespace!;

  private readonly Counter<long> myRequestsCounter;
  private readonly Counter<long> myRedirectSignatureCounter;

  public CachingProxyMetrics(IMeterFactory meterFactory)
  {
    Meter = meterFactory.Create(MeterName);
    myRequestsCounter = Meter.CreateCounter<long>("caching_requests");
    myRedirectSignatureCounter = Meter.CreateCounter<long>("caching_redirect_signature_verifications");
  }

  public Meter Meter { get; }

  public void IncrementRequests(CachingProxyStatus status)
  {
    myRequestsCounter.Add(1, new KeyValuePair<string, object?>(nameof(status), status.ToString()));
  }

  /// <summary>
  /// Records which key in the rotation ring validated a redirect signature (see
  /// <see cref="RedirectSignatureKeyRing"/>). This is what makes a rotation finishable: the retiring key
  /// may only be dropped once <c>key_role="retiring"</c> stops appearing, and a <c>"mismatch"</c> spike
  /// means the signer moved to a key this side does not hold. Cardinality is bounded by the ring size.
  /// </summary>
  /// <param name="keyRole">"active", "retiring", or "mismatch".</param>
  /// <param name="keyFingerprint">Fingerprint of the matched key, or "none" when nothing matched.</param>
  public void IncrementRedirectSignatureVerification(string keyRole, string keyFingerprint)
  {
    myRedirectSignatureCounter.Add(1,
      new KeyValuePair<string, object?>("key_role", keyRole),
      new KeyValuePair<string, object?>("key_fingerprint", keyFingerprint));
  }
}
