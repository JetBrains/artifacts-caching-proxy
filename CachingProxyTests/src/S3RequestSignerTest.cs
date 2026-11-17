using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// S3 upstreams are signed with SigV4 as the proxy's own AWS identity (MRI-4463).
public class S3RequestSignerTest
{
  [Theory]
  [InlineData("https://jetbrains-rider-sdk-nightly-nuget.s3.eu-west-1.amazonaws.com/index.json", "jetbrains-rider-sdk-nightly-nuget", "eu-west-1")]
  [InlineData("https://intellij-nightly-sdk.s3-eu-west-1.amazonaws.com/x", "intellij-nightly-sdk", "eu-west-1")]
  [InlineData("https://a.b-c.s3.us-gov-west-1.amazonaws.com/", "a.b-c", "us-gov-west-1")]
  public void The_Bucket_Is_Parsed_From_A_Virtual_Host(string uri, string name, string region) =>
    Assert.Equal(new S3Bucket(name, region), S3Bucket.FromUri(new Uri(uri)));

  [Theory]
  [InlineData("https://s3.eu-west-1.amazonaws.com/bucket/key")]
  [InlineData("https://bucket.s3.amazonaws.com/key")]
  [InlineData("https://packages-151453349183.d.codeartifact.eu-west-1.amazonaws.com/nuget/net-build/")]
  [InlineData("https://bucket.s3.eu-west-1.amazonaws.com.example.com/key")]
  [InlineData("https://packages.jetbrains.team/nuget/p/net/build/")]
  public void Other_Hosts_Are_No_S3_Buckets(string uri) => Assert.Null(S3Bucket.FromUri(new Uri(uri)));

  // https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-header-based-auth.html, "Example: GET Object".
  [Fact]
  public void The_Published_Aws_Example_Is_Reproduced()
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/test.txt");
    request.Headers.Range = new RangeHeaderValue(0, 9);

    S3RequestSigner.Sign(request,
      new ImmutableCredentials("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", null),
      "us-east-1", new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero), "range");

    Assert.Equal(
      "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
      "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
      "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
      request.Headers.GetValues("Authorization").Single());
  }

  [Fact]
  public void A_Session_Token_Is_Sent_And_Signed()
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://b.s3.eu-west-1.amazonaws.com/k");
    S3RequestSigner.Sign(request, new ImmutableCredentials("AKID", "secret", "session+token/="), "eu-west-1",
      DateTimeOffset.UnixEpoch);

    Assert.Equal("session+token/=", request.Headers.GetValues("x-amz-security-token").Single());
    Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date;x-amz-security-token,",
      request.Headers.GetValues("Authorization").Single());
  }

  [Fact]
  public void Conditional_Headers_Stay_Out_Of_The_Signature()
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://b.s3.eu-west-1.amazonaws.com/k");
    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"etag\""));
    request.Headers.Accept.ParseAdd("application/json");
    S3RequestSigner.Sign(request, new ImmutableCredentials("AKID", "secret", null), "eu-west-1", DateTimeOffset.UnixEpoch);

    Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date,", request.Headers.GetValues("Authorization").Single());
  }

  [Theory]
  // Already canonical: unchanged.
  [InlineData("/flatcontainer/jetbrains.lifetimes/2027.1.0/jetbrains.lifetimes.2027.1.0.nupkg",
    "/flatcontainer/jetbrains.lifetimes/2027.1.0/jetbrains.lifetimes.2027.1.0.nupkg")]
  // Reserved characters are encoded once, an escape is not encoded twice, '~' stays.
  [InlineData("/a+b/c%20d/e~f", "/a%2Bb/c%20d/e~f")]
  public void The_Uri_Sent_Is_The_Canonical_One(string path, string expected)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://b.s3.eu-west-1.amazonaws.com" + path);
    S3RequestSigner.Sign(request, new ImmutableCredentials("AKID", "secret", null), "eu-west-1", DateTimeOffset.UnixEpoch);

    Assert.Equal(expected, request.RequestUri!.AbsolutePath);
  }

  [Fact]
  public async Task Credentials_Come_From_The_Source_And_The_Date_From_The_Clock()
  {
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    var calls = 0;
    var signer = new S3RequestSigner(time, _ =>
    {
      calls++;
      return Task.FromResult(new ImmutableCredentials("AKID", "secret", null));
    });

    using var request = new HttpRequestMessage(HttpMethod.Get, "https://b.s3.eu-west-1.amazonaws.com/k");
    await signer.SignAsync(request, new S3Bucket("b", "eu-west-1"), CancellationToken.None);

    Assert.Equal(1, calls);
    Assert.Equal("20261117T120000Z", request.Headers.GetValues("x-amz-date").Single());
    Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKID/20261117/eu-west-1/s3/aws4_request,",
      request.Headers.GetValues("Authorization").Single());
  }
}
