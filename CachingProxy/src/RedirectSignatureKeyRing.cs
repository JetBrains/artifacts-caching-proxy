using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JetBrains.CachingProxy;

/// <summary>
/// The HMAC keys a redirect signature may be validated against, parsed once at startup from
/// <see cref="CachingProxyConfig.RedirectSignatureConfig.Key"/>.
/// <para>
/// It is a set rather than a single key so that key rotation has an overlap window: the signer
/// (cache-redirector) is in a different AWS account, reads its key only at startup, and rolls over task by
/// task, so holding the retiring key here keeps signatures from not-yet-restarted signers working. Nothing
/// on the wire identifies a key (adding an identifier would have broken a contract spanning two repos), so
/// each is simply tried — a handful of HMAC-SHA256 computations over a request line.
/// </para>
/// </summary>
public sealed class RedirectSignatureKeyRing
{
  // Below this, an attacker who observes one signed URL — knowing both payload and MAC — can recover the
  // key offline and mint signatures for arbitrary private paths. The redirector enforces the same floor.
  public const int MinimumKeyBytes = 32;

  /// <summary>One key: UTF-8 bytes pre-encoded (this is the per-request path), plus its fingerprint.</summary>
  public sealed record Key(byte[] Bytes, string Fingerprint);

  private RedirectSignatureKeyRing(Key[] keys) => Keys = keys;

  /// <summary>
  /// The keys to try, in configured order. The first is the <em>active</em> one the redirector is expected
  /// to be signing with; a match on a later key means a signer has not rolled over yet, which
  /// <see cref="CachingProxyMetrics.IncrementRedirectSignatureVerification"/> counts.
  /// </summary>
  public Key[] Keys { get; }

  /// <summary>
  /// Splits a configured value into keys on any whitespace, so a lone key yields a single-element ring and
  /// the pre-rotation configuration keeps working. Must match <c>auth.parse_keys</c> in the redirector.
  /// </summary>
  public static string[] Split(string? raw) =>
    (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

  /// <summary>
  /// First 4 bytes of SHA-256(key), hex: too short to attack the key, enough to identify it in logs and
  /// metrics. Must match <c>auth.key_fingerprint</c> in the redirector, otherwise the two accounts'
  /// fingerprints cannot be compared during a rotation.
  /// </summary>
  public static string Fingerprint(string key) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))[..4]).ToLowerInvariant();

  /// <summary>
  /// Parses and validates the configured value, failing startup (like the rest of
  /// <see cref="AuthExtensions.AddInboundAuth"/>) rather than silently dropping a bad key — a ring that
  /// quietly lost the retiring key would 401 every artifact still signed with it.
  /// </summary>
  public static RedirectSignatureKeyRing Parse(string? raw)
  {
    var keys = Split(raw);
    if (keys.Length == 0)
      throw new ArgumentException(
        "InboundAuth.RedirectSignature.Key must contain at least one key (whitespace-separated).");

    var tooShort = keys.Where(key => Encoding.UTF8.GetByteCount(key) < MinimumKeyBytes).ToArray();
    if (tooShort.Length > 0)
      throw new ArgumentException(
        $"InboundAuth.RedirectSignature.Key contains {tooShort.Length} key(s) shorter than " +
        $"{MinimumKeyBytes} UTF-8 bytes (fingerprints: {string.Join(", ", tooShort.Select(Fingerprint))}). " +
        "Note the value is split on whitespace, so a key containing a space reads as two short keys.");

    // Almost always the new key pasted into both slots mid-rotation, which looks like an overlap window
    // but is not one.
    var duplicate = keys.GroupBy(key => key).FirstOrDefault(group => group.Count() > 1);
    if (duplicate != null)
      throw new ArgumentException(
        $"InboundAuth.RedirectSignature.Key lists the same key more than once (fingerprint: " +
        $"{Fingerprint(duplicate.Key)}). Use one entry per distinct key.");

    return new RedirectSignatureKeyRing(
      [.. keys.Select(key => new Key(Encoding.UTF8.GetBytes(key), Fingerprint(key)))]);
  }

  /// <summary>Fingerprints in configured order, active first — for diagnostic logging.</summary>
  public override string ToString() => string.Join(", ", Keys.Select(key => key.Fingerprint));
}
