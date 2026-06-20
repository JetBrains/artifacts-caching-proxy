using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;
using ZiggyCreatures.Caching.Fusion.Serialization;
using ZiggyCreatures.Caching.Fusion.Serialization.CysharpMemoryPack;

namespace JetBrains.CachingProxy.Tests;

public class CachedResponseSerializationTest
{
  private readonly IFusionCacheSerializer _serializer;

  public CachedResponseSerializationTest()
  {
    // The same registration the app performs when L2 is wired; required for the custom formatter
    // that handles CachedResponse's IHeaderDictionary.
    CachedResponseFormatter.Register();
    _serializer = new FusionCacheCysharpMemoryPackSerializer();
  }

  private CachedResponse RoundTrip(CachedResponse original)
  {
    var bytes = _serializer.Serialize(original);
    var result = _serializer.Deserialize<CachedResponse>(bytes);
    Assert.NotNull(result);
    return result!;
  }

  [Fact]
  public void RoundTrip_PreservesStatusHeadersAndBody()
  {
    IHeaderDictionary headers = new HeaderDictionary();
    headers.ContentType = "application/java-archive";
    headers.ContentLength = 4;
    headers.ETag = "\"deadbeef\"";
    // Multi-valued header: the riskiest case for the StringValues <-> string[] mapping.
    headers.ContentEncoding = new StringValues(["gzip", "br"]);
    headers["X-Custom"] = "custom-value";

    var original = new CachedResponse(HttpStatusCode.OK, headers, [1, 2, 3, 4]);

    var result = RoundTrip(original);

    Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Body);
    Assert.Equal("application/java-archive", result.Headers.ContentType);
    Assert.Equal(4, result.Headers.ContentLength);
    Assert.Equal("\"deadbeef\"", result.Headers.ETag);
    Assert.Equal(new StringValues(["gzip", "br"]), result.Headers.ContentEncoding);
    Assert.Equal("custom-value", result.Headers["X-Custom"]);
  }

  [Fact]
  public void RoundTrip_PreservesNullBody()
  {
    // HEAD-style metadata entry: status + headers, no body.
    IHeaderDictionary headers = new HeaderDictionary();
    headers.ContentType = "text/plain";
    headers.ContentLength = 100;
    var original = new CachedResponse(HttpStatusCode.NotFound, headers);

    var result = RoundTrip(original);

    Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    Assert.Null(result.Body);
    Assert.Equal("text/plain", result.Headers.ContentType);
    Assert.Equal(100, result.Headers.ContentLength);
  }

  [Fact]
  public void RoundTrip_PreservesStaticInstanceBody()
  {
    var result = RoundTrip(CachedResponse.InvalidPath);

    Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    Assert.Equal(CachedResponse.InvalidPath.Body, result.Body);
  }
}
