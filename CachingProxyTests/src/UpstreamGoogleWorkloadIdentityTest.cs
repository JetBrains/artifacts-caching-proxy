using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZiggyCreatures.Caching.Fusion;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// Google workload identity mode: the AWS identity is exchanged for a Google federated token at the configured
// pool provider. Covers what startup rejects, the subject token Google replays to AWS, the exchange call, the
// header shape and token caching.
public class UpstreamGoogleWorkloadIdentityTest
{
  private const string Audience =
    "//iam.googleapis.com/projects/544788055534/locations/global/workloadIdentityPools/artifacts-caching-proxy/providers/aws-proxy-roles";
  private const string UrlPrefix = "europe-west1-maven.pkg.dev/jetbrains-cache-redirector/maven-all/";

  private static void Configure(params (string Key, string Value)[] settings)
  {
    var values = new Dictionary<string, string?> { ["UpstreamAuth:gar:UrlPrefixes:0"] = UrlPrefix };
    foreach (var (key, value) in settings) values["UpstreamAuth:gar:" + key] = value;
    new ServiceCollection().AddUpstreamAuth(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
  }

  [Fact]
  public void An_Audience_Alone_Is_A_Complete_Entry() => Configure(("GoogleWorkloadIdentityAudience", Audience));

  [Theory]
  [InlineData("projects/544788055534/locations/global/workloadIdentityPools/p/providers/x")]
  [InlineData("https://iam.googleapis.com/projects/544788055534/locations/global/workloadIdentityPools/p/providers/x")]
  [InlineData("//iam.googleapis.com/projects/544788055534/locations/global/workloadIdentityPools/p")]
  [InlineData("artifacts-caching-proxy@jetbrains-cache-redirector.iam.gserviceaccount.com")]
  public void Something_Other_Than_A_Provider_Is_Rejected(string audience)
  {
    var exception = Assert.Throws<ArgumentException>(() => Configure(("GoogleWorkloadIdentityAudience", audience)));
    Assert.Contains("no workload identity pool provider", exception.Message);
  }

  [Theory]
  [InlineData("ClientId", "svc-proxy")]
  [InlineData("Username", "_json_key_base64")]
  [InlineData("Password", "key")]
  [InlineData("PrivateKey", "-----BEGIN RSA PRIVATE KEY-----")]
  [InlineData("UrlPrefixes:1", "packages-151453349183.d.codeartifact.eu-west-1.amazonaws.com/nuget/x/")]
  public void Mixing_With_Another_Mode_Is_Rejected(string key, string value)
  {
    var exception = Assert.Throws<ArgumentException>(() =>
      Configure(("GoogleWorkloadIdentityAudience", Audience), (key, value)));
    Assert.Contains("authenticates one way only", exception.Message);
  }

  [Fact]
  public void An_Audience_Counts_As_A_Credential() =>
    // So its prefixes are gated like every other credentialed upstream.
    Assert.True(new UpstreamAuth { UrlPrefixes = [UrlPrefix], GoogleWorkloadIdentityAudience = Audience }.HasCredential);

  [Fact]
  public void The_Audience_Is_Rendered() =>
    Assert.Contains(Audience, new UpstreamAuth { UrlPrefixes = [UrlPrefix], GoogleWorkloadIdentityAudience = Audience }.ToString());

  private static readonly DateTimeOffset ourNow = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

  private static Dictionary<string, string> DecodeSubjectToken(string subjectToken, out string url, out string method)
  {
    using var json = JsonDocument.Parse(Uri.UnescapeDataString(subjectToken));
    url = json.RootElement.GetProperty("url").GetString()!;
    method = json.RootElement.GetProperty("method").GetString()!;
    return json.RootElement.GetProperty("headers").EnumerateArray()
      .ToDictionary(static h => h.GetProperty("key").GetString()!, static h => h.GetProperty("value").GetString()!);
  }

  [Fact]
  public void The_Subject_Token_Is_A_Signed_GetCallerIdentity_Bound_To_The_Provider()
  {
    var token = GoogleFederatedTokenClient.CreateSubjectToken(
      new ImmutableCredentials("AKID", "secret", "session+token/="), Audience, ourNow);

    var headers = DecodeSubjectToken(token, out var url, out var method);
    Assert.Equal("https://sts.amazonaws.com?Action=GetCallerIdentity&Version=2011-06-15", url);
    Assert.Equal("POST", method);
    Assert.Equal("sts.amazonaws.com", headers["host"]);
    Assert.Equal("20261127T120000Z", headers["x-amz-date"]);
    Assert.Equal("session+token/=", headers["x-amz-security-token"]);
    Assert.Equal(Audience, headers["x-goog-cloud-target-resource"]);
    // Signing it makes Google STS answer "doesn't contain all the required headers".
    Assert.DoesNotContain("x-amz-content-sha256", headers.Keys);
    // Computed independently, by the implementation Google accepted from the probe task.
    Assert.Equal(
      "AWS4-HMAC-SHA256 Credential=AKID/20261127/us-east-1/sts/aws4_request, " +
      "SignedHeaders=host;x-amz-date;x-amz-security-token;x-goog-cloud-target-resource, " +
      "Signature=c616550fd00386980e875405d20e63c83704f104e1ab15ebb3ae5cb1d619c83e",
      headers["Authorization"]);
  }

  [Fact]
  public void Long_Term_Credentials_Send_No_Session_Token()
  {
    var token = GoogleFederatedTokenClient.CreateSubjectToken(new ImmutableCredentials("AKID", "secret", null), Audience, ourNow);

    Assert.DoesNotContain("x-amz-security-token", DecodeSubjectToken(token, out _, out _).Keys);
  }

  private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
  {
    public HttpRequestMessage? Request;
    public string? RequestBody;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Request = request;
      RequestBody = await request.Content!.ReadAsStringAsync(ct);
      return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
  }

  private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
  }

  private static GoogleFederatedTokenClient CreateClient(StubHandler handler) =>
    new(new StubHttpClientFactory(handler), new FakeTimeProvider(ourNow),
      static _ => Task.FromResult(new ImmutableCredentials("AKID", "secret", "session")));

  [Fact]
  public async Task The_Exchange_Asks_Google_STS_For_An_Access_Token()
  {
    var handler = new StubHandler(HttpStatusCode.OK, """{"access_token":"ya29.tok","token_type":"Bearer","expires_in":899}""");

    var (token, expiresAt) = await CreateClient(handler).ExchangeAsync(Audience, CancellationToken.None);

    Assert.Equal("ya29.tok", token);
    Assert.Equal(ourNow + TimeSpan.FromSeconds(899), expiresAt);
    Assert.Equal(HttpMethod.Post, handler.Request!.Method);
    Assert.Equal("https://sts.googleapis.com/v1/token", handler.Request.RequestUri!.ToString());
    using var body = JsonDocument.Parse(handler.RequestBody!);
    var fields = body.RootElement;
    Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", fields.GetProperty("grant_type").GetString());
    Assert.Equal(Audience, fields.GetProperty("audience").GetString());
    Assert.Equal("https://www.googleapis.com/auth/cloud-platform", fields.GetProperty("scope").GetString());
    Assert.Equal("urn:ietf:params:oauth:token-type:access_token", fields.GetProperty("requested_token_type").GetString());
    Assert.Equal("urn:ietf:params:aws:token-type:aws4_request", fields.GetProperty("subject_token_type").GetString());
    Assert.Equal(Audience,
      DecodeSubjectToken(fields.GetProperty("subject_token").GetString()!, out _, out _)["x-goog-cloud-target-resource"]);
  }

  [Fact]
  public async Task A_Refused_Exchange_Names_Google_s_Reason()
  {
    var handler = new StubHandler(HttpStatusCode.BadRequest,
      """{"error":"unauthorized_client","error_description":"The given credential is rejected by the attribute condition."}""");

    var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      CreateClient(handler).ExchangeAsync(Audience, CancellationToken.None));

    Assert.Contains("400", exception.Message);
    Assert.Contains("rejected by the attribute condition", exception.Message);
  }

  private sealed class FakeTokenClient(FakeTimeProvider time) : IGoogleFederatedTokenClient
  {
    public readonly List<string> Calls = [];

    public Task<(string Token, DateTimeOffset ExpiresAt)> ExchangeAsync(string audience, CancellationToken ct)
    {
      Calls.Add(audience);
      return Task.FromResult(($"ya29.tok-{Calls.Count}", time.GetUtcNow() + TimeSpan.FromSeconds(899)));
    }
  }

  // FusionCache takes expirations from the system clock, the memory cache compares them on the fake one, so the
  // fake starts at the real now (see ResponseCacheTest).
  private static (UpstreamAuthorizationProvider Provider, FakeTokenClient Client, FakeTimeProvider Time) CreateProvider()
  {
    var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var client = new FakeTokenClient(time);
    var cache = new FusionCache(new FusionCacheOptions(),
      new MemoryCache(new MemoryCacheOptions { Clock = new TimeProviderClock(time) }));
    var tokens = new GoogleWorkloadIdentityTokenProvider(client, cache, time, NullLogger<GoogleWorkloadIdentityTokenProvider>.Instance);
    // Only the Google provider: this mode resolves nothing else.
    return (new UpstreamAuthorizationProvider(null!, googleTokenProvider: tokens), client, time);
  }

  private static readonly UpstreamAuth ourAuth = new() { UrlPrefixes = [UrlPrefix], GoogleWorkloadIdentityAudience = Audience };

  [Fact]
  public async Task The_Token_Is_The_Basic_Password_Of_User_Oauth2accesstoken()
  {
    var (provider, client, _) = CreateProvider();

    var header = await provider.GetAuthorizationHeaderAsync(ourAuth, CancellationToken.None);

    Assert.NotNull(header);
    Assert.Equal("Basic", header.Scheme);
    Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("oauth2accesstoken:ya29.tok-1")), header.Parameter);
    Assert.Equal(Audience, Assert.Single(client.Calls));
  }

  [Fact]
  public async Task The_Token_Is_Cached_And_Exchanged_Again_Before_Expiry()
  {
    var (provider, client, time) = CreateProvider();

    var first = await provider.GetAuthorizationHeaderAsync(ourAuth, CancellationToken.None);
    time.Advance(TimeSpan.FromMinutes(9));
    var cached = await provider.GetAuthorizationHeaderAsync(ourAuth, CancellationToken.None);
    Assert.Equal(first!.Parameter, cached!.Parameter);
    Assert.Single(client.Calls);

    // Inside the 5-minute refresh window of the 15-minute token: exchanged again while the old one still works.
    time.Advance(TimeSpan.FromMinutes(2));
    var refreshed = await provider.GetAuthorizationHeaderAsync(ourAuth, CancellationToken.None);
    Assert.Equal(2, client.Calls.Count);
    Assert.NotEqual(first.Parameter, refreshed!.Parameter);
  }
}
