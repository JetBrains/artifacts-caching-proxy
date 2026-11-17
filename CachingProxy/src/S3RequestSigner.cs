using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;

namespace JetBrains.CachingProxy;

/// <summary>
/// A bucket named by a virtual-hosted S3 host: <c>&lt;bucket&gt;.s3.&lt;region&gt;.amazonaws.com</c>, or the
/// legacy <c>&lt;bucket&gt;.s3-&lt;region&gt;.amazonaws.com</c>. Path-style and global endpoints carry no region
/// in the host and are not recognized, so they are fetched anonymously as before.
/// </summary>
public sealed partial record S3Bucket(string Name, string Region)
{
  public static S3Bucket? FromUri(Uri uri)
  {
    var match = OurVirtualHost.Match(uri.Host);
    return match.Success ? new S3Bucket(match.Groups["bucket"].Value, match.Groups["region"].Value) : null;
  }

  // Region names are letters, digits and hyphens; "s3-external-1" and dualstack are not matched.
  [GeneratedRegex(@"^(?<bucket>[a-z0-9][a-z0-9.-]{1,61}[a-z0-9])\.s3[.-](?<region>[a-z]{2}(-[a-z]+)+-\d)\.amazonaws\.com$",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex OurVirtualHost { get; }
}

/// <summary>The AWS identity the proxy signs with, behind a delegate so tests need no AWS.</summary>
public delegate Task<ImmutableCredentials> AwsCredentialSource(CancellationToken ct);

/// <summary>
/// Signs requests to an S3 upstream with SigV4, as the proxy's own AWS identity from the SDK's default chain: the
/// ECS task or EC2 instance role on AWS, web identity (<c>AWS_ROLE_ARN</c> + <c>AWS_WEB_IDENTITY_TOKEN_FILE</c>)
/// off it. So a private bucket that grants those roles read access is served like any public upstream, with no
/// stored key. Not an <see cref="UpstreamAuth"/> mode: an alias reaching it is as public as its configuration
/// says, and the bucket policy, not the proxy, decides which buckets the identity can read.
/// <para>Signed headers are <c>host</c> and the <c>x-amz-*</c> ones only, so the conditional and Accept headers
/// added for revalidation stay out of the signature.</para>
/// </summary>
public sealed class S3RequestSigner(TimeProvider timeProvider, AwsCredentialSource? credentialSource = null)
{
  // Of the empty body: every request here is a GET or a HEAD.
  private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

  // The shared resolver caches the chain's credentials object, which refreshes itself before it expires.
  private readonly AwsCredentialSource myCredentialSource = credentialSource ??
    (static ct => DefaultAWSCredentialsIdentityResolver.GetCredentials().GetCredentialsAsync());

  public async Task SignAsync(HttpRequestMessage request, S3Bucket bucket, CancellationToken ct)
  {
    var credentials = await myCredentialSource(ct);
    Sign(request, credentials, bucket.Region, timeProvider.GetUtcNow());
  }

  /// <summary>
  /// Adds the SigV4 headers to <paramref name="request"/>. Rewrites its URI to the canonical encoding first, so
  /// what goes on the wire is exactly what was signed. <paramref name="extraSignedHeaders"/> exists for the
  /// published AWS example, which signs <c>range</c> too.
  /// </summary>
  public static void Sign(HttpRequestMessage request, ImmutableCredentials credentials, string region,
    DateTimeOffset now, params string[] extraSignedHeaders)
  {
    var uri = request.RequestUri ?? throw new ArgumentException("Request has no URI.", nameof(request));
    var path = CanonicalPath(uri.AbsolutePath);
    var query = CanonicalQuery(uri.Query);
    request.RequestUri = new Uri($"{uri.Scheme}://{uri.Authority}{path}{(query.Length > 0 ? "?" + query : "")}");

    var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    var date = amzDate[..8];
    request.Headers.Remove("x-amz-date");
    request.Headers.Remove("x-amz-content-sha256");
    request.Headers.Remove("x-amz-security-token");
    request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
    request.Headers.TryAddWithoutValidation("x-amz-content-sha256", EmptyPayloadHash);
    if (credentials.UseToken)
      request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.Token);

    var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
      ["host"] = uri.IsDefaultPort ? uri.Host : uri.Authority,
    };
    foreach (var (name, values) in request.Headers)
    {
      var lower = name.ToLowerInvariant();
      if (lower.StartsWith("x-amz-", StringComparison.Ordinal) || extraSignedHeaders.Contains(lower))
        signed[lower] = string.Join(",", values.Select(static v => v.Trim()));
    }

    var signedHeaders = string.Join(";", signed.Keys);
    var canonicalRequest = string.Join("\n",
      request.Method.Method,
      path,
      query,
      string.Concat(signed.Select(static h => $"{h.Key}:{h.Value}\n")),
      signedHeaders,
      EmptyPayloadHash);

    var scope = $"{date}/{region}/s3/aws4_request";
    var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

    var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretKey), date);
    key = Hmac(key, region);
    key = Hmac(key, "s3");
    key = Hmac(key, "aws4_request");
    var signature = Hex(Hmac(key, stringToSign));

    request.Headers.TryAddWithoutValidation("Authorization",
      $"AWS4-HMAC-SHA256 Credential={credentials.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
  }

  // S3 keys are encoded once, segment by segment, and '/' is kept.
  private static string CanonicalPath(string absolutePath) =>
    string.Join("/", absolutePath.Split('/').Select(static segment => Encode(Uri.UnescapeDataString(segment))));

  private static string CanonicalQuery(string query)
  {
    if (query.Length <= 1) return "";
    return string.Join("&", query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries)
      .Select(static pair =>
      {
        var eq = pair.IndexOf('=');
        var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
        var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        return (Name: Encode(name), Value: Encode(value));
      })
      .OrderBy(static p => p.Name, StringComparer.Ordinal).ThenBy(static p => p.Value, StringComparer.Ordinal)
      .Select(static p => $"{p.Name}={p.Value}"));
  }

  // RFC 3986 unreserved characters stay, everything else is %XX of its UTF-8 bytes, upper-case.
  private static string Encode(string value)
  {
    var builder = new StringBuilder(value.Length);
    foreach (var b in Encoding.UTF8.GetBytes(value))
    {
      if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
          or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
        builder.Append((char)b);
      else
        builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
    }

    return builder.ToString();
  }

  private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

  private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
