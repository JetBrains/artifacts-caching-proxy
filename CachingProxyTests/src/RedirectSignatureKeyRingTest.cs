using System;
using System.Linq;
using System.Text;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// Unit coverage for the rotation key ring: how InboundAuth:RedirectSignature:Key splits into keys, what is
// rejected at startup, and the fingerprints used to observe a rotation.
//
// Both are a cross-repo contract with the cache-redirector's auth.parse_keys / auth.key_fingerprint: the
// same secret value goes to both AWS accounts and must yield the same keys in the same order, and the
// fingerprints are only comparable across the two sides if computed identically. The expectations below are
// therefore openssl-derived and duplicated verbatim in that repo's test/auth_test.lua.
public class RedirectSignatureKeyRingTest
{
  private const string KeyA = "super-secret-shared-hmac-key-32bytes";
  private const string KeyB = "the-next-shared-hmac-key-32bytes-ok";

  [Fact]
  public void Single_Key_Parses_To_A_One_Element_Ring()
  {
    // The pre-rotation configuration: one key, no whitespace. Must keep working untouched.
    var ring = RedirectSignatureKeyRing.Parse(KeyA);

    Assert.Single(ring.Keys);
    Assert.Equal(Encoding.UTF8.GetBytes(KeyA), ring.Keys[0].Bytes);
  }

  [Fact]
  public void Multiple_Keys_Keep_Configured_Order()
  {
    // Order is meaningful: the first key is the active one, and matching a later one is what the
    // "retiring key" warning and metric are keyed off.
    var ring = RedirectSignatureKeyRing.Parse($"{KeyB} {KeyA}");

    Assert.Equal(
      [RedirectSignatureKeyRing.Fingerprint(KeyB), RedirectSignatureKeyRing.Fingerprint(KeyA)],
      ring.Keys.Select(key => key.Fingerprint));
  }

  [Theory]
  // The value is hand-edited into `aws secretsmanager put-secret-value` during a rotation, so a stray
  // newline or double space is easy to introduce and must not produce a bogus ring.
  [InlineData("  a  b \n")]
  [InlineData("a\tb")]
  [InlineData("a\nb")]
  [InlineData("a b")]
  public void Whitespace_Runs_Separate_Keys(string raw)
  {
    Assert.Equal(["a", "b"], RedirectSignatureKeyRing.Split(raw));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   \t\n ")]
  public void Empty_Or_Whitespace_Only_Value_Is_Rejected(string? raw)
  {
    // An empty ring would silently 401 every signed redirect, so fail startup instead.
    var exception = Assert.Throws<ArgumentException>(() => RedirectSignatureKeyRing.Parse(raw));

    Assert.Contains("at least one key", exception.Message);
  }

  [Fact]
  public void Key_Shorter_Than_The_Minimum_Is_Rejected()
  {
    var exception = Assert.Throws<ArgumentException>(() => RedirectSignatureKeyRing.Parse("too-short"));

    Assert.Contains($"{RedirectSignatureKeyRing.MinimumKeyBytes} UTF-8 bytes", exception.Message);
  }

  [Fact]
  public void One_Short_Key_Rejects_The_Whole_Ring()
  {
    // Dropping just the bad key would leave an operator believing in an overlap window that is not there.
    var exception = Assert.Throws<ArgumentException>(() => RedirectSignatureKeyRing.Parse($"{KeyA} short"));

    Assert.Contains("1 key(s) shorter", exception.Message);
  }

  [Fact]
  public void Key_Containing_A_Space_Is_Rejected_As_Two_Short_Keys()
  {
    // The failure mode the whitespace separator introduces. Both fragments are under the minimum length,
    // so it is caught at startup rather than becoming a silent authentication failure.
    var exception = Assert.Throws<ArgumentException>(
      () => RedirectSignatureKeyRing.Parse("half-of-a-key-here other-half-of-that-key"));

    Assert.Contains("2 key(s) shorter", exception.Message);
    Assert.Contains("split on whitespace", exception.Message);
  }

  [Fact]
  public void Duplicate_Key_Is_Rejected()
  {
    // Almost always the new key pasted into both slots mid-rotation, which provides no overlap at all.
    var exception = Assert.Throws<ArgumentException>(() => RedirectSignatureKeyRing.Parse($"{KeyA} {KeyA}"));

    Assert.Contains("more than once", exception.Message);
  }

  [Theory]
  // Openssl-derived, NOT produced by this code:
  //     printf '%s' "$key" | openssl dgst -sha256 -binary | xxd -p | head -c 8
  // Asserted in the cache-redirector's test/auth_test.lua too, so either side drifting fails both.
  [InlineData("test-key", "62af8704")]
  [InlineData("s3cr3t", "4e738ca5")]
  [InlineData(KeyA, "a4c29535")]
  public void Fingerprint_Matches_Known_Vectors(string key, string expected)
  {
    Assert.Equal(expected, RedirectSignatureKeyRing.Fingerprint(key));
  }

  [Fact]
  public void Fingerprint_Is_Eight_Lowercase_Hex_Characters()
  {
    // Truncated to 4 bytes on purpose: enough to identify a key in logs, far too little to attack it.
    var fingerprint = RedirectSignatureKeyRing.Fingerprint(KeyA);

    Assert.Equal(8, fingerprint.Length);
    Assert.Matches("^[0-9a-f]{8}$", fingerprint);
  }

  [Fact]
  public void Fingerprint_Distinguishes_Keys()
  {
    Assert.NotEqual(RedirectSignatureKeyRing.Fingerprint(KeyA), RedirectSignatureKeyRing.Fingerprint(KeyB));
  }

  [Fact]
  public void ToString_Lists_Fingerprints_Active_First()
  {
    // Used in the "signature mismatch" log line, the main diagnostic for out-of-order rotation steps.
    var ring = RedirectSignatureKeyRing.Parse($"{KeyB} {KeyA}");

    Assert.Equal(
      $"{RedirectSignatureKeyRing.Fingerprint(KeyB)}, {RedirectSignatureKeyRing.Fingerprint(KeyA)}",
      ring.ToString());
  }
}
