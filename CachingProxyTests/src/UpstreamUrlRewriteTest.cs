using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class UpstreamUrlRewriteTest
{
  private static readonly UpstreamUrlRewrite ourSpace = new(
    new Uri("https://packages.jetbrains.team/nuget/p/net/build/"),
    new Uri("https://cache-redirector.jetbrains.com/nuget-build/"));

  private static string? Rewrite(string json) =>
    ourSpace.Rewrite(Encoding.UTF8.GetBytes(json)) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

  [Fact]
  public void Rewrites_Every_String_That_Starts_With_The_Root()
  {
    Assert.Equal(
      """{"@id":"https://cache-redirector.jetbrains.com/nuget-build/v3/registration1/a/index.json","packageContent":"https://cache-redirector.jetbrains.com/nuget-build/v3/flatcontainer/a/1.0.0/a.1.0.0.nupkg"}""",
      Rewrite("""{"@id":"https://packages.jetbrains.team/nuget/p/net/build/v3/registration1/a/index.json","packageContent":"https://packages.jetbrains.team/nuget/p/net/build/v3/flatcontainer/a/1.0.0/a.1.0.0.nupkg"}"""));
  }

  [Fact]
  public void Rewrites_The_Escaped_Slash_Spelling()
  {
    Assert.Equal(
      """{"@id":"https:\/\/cache-redirector.jetbrains.com\/nuget-build\/v3\/x"}""",
      Rewrite("""{"@id":"https:\/\/packages.jetbrains.team\/nuget\/p\/net\/build\/v3\/x"}"""));
  }

  [Theory]
  // Mid-string: text that quotes a URL, not a link.
  [InlineData("""{"description":"see https://packages.jetbrains.team/nuget/p/net/build/v3/x"}""")]
  // A sibling feed: the root ends in '/', so build-tools is not under it.
  [InlineData("""{"@id":"https://packages.jetbrains.team/nuget/p/net/build-tools/v3/x"}""")]
  // Another feed, another scheme, another host.
  [InlineData("""{"@id":"https://packages.jetbrains.team/nuget/p/net/test-data/v3/x"}""")]
  [InlineData("""{"@id":"http://packages.jetbrains.team/nuget/p/net/build/v3/x"}""")]
  [InlineData("""{"projectUrl":"https://www.jetbrains.com/"}""")]
  public void Leaves_Everything_Else_Alone(string json) => Assert.Null(Rewrite(json));

  [Fact]
  public async Task Gzip_Body_Is_Rewritten_And_Reencoded()
  {
    const string json = """{"@id":"https://packages.jetbrains.team/nuget/p/net/build/v3/x"}""";
    using var response = Response(Gzip(json), "application/json", "gzip");

    await ourSpace.ApplyAsync(response, Timeout.InfiniteTimeSpan, CancellationToken.None);

    var content = Assert.IsType<RewrittenContent>(response.Content);
    Assert.Equal("gzip", content.Headers.ContentEncoding.Single());
    Assert.Equal("""{"@id":"https://cache-redirector.jetbrains.com/nuget-build/v3/x"}""", Gunzip(await content.ReadAsByteArrayAsync()));
  }

  [Fact]
  public async Task Unchanged_Body_Keeps_Its_Bytes_And_Is_Not_Marked_Rewritten()
  {
    const string json = """{"versions":["1.0.0"]}""";
    using var response = Response(Encoding.UTF8.GetBytes(json), "application/json");

    await ourSpace.ApplyAsync(response, Timeout.InfiniteTimeSpan, CancellationToken.None);

    Assert.IsNotType<RewrittenContent>(response.Content);
    Assert.Equal(json, await response.Content.ReadAsStringAsync());
    Assert.Equal(json.Length, response.Content.Headers.ContentLength);
  }

  [Fact]
  public async Task Non_Json_Is_Not_Read()
  {
    const string text = "https://packages.jetbrains.team/nuget/p/net/build/v3/x";
    using var response = Response(Encoding.UTF8.GetBytes($"\"{text}"), "text/plain");
    var original = response.Content;

    await ourSpace.ApplyAsync(response, Timeout.InfiniteTimeSpan, CancellationToken.None);

    Assert.Same(original, response.Content);
  }

  [Fact]
  public async Task Undecodable_Gzip_Is_Kept_As_Sent()
  {
    var garbage = "not gzip at all"u8.ToArray();
    using var response = Response(garbage, "application/json", "gzip");

    await ourSpace.ApplyAsync(response, Timeout.InfiniteTimeSpan, CancellationToken.None);

    Assert.Equal(garbage, await response.Content.ReadAsByteArrayAsync());
  }

  [Fact]
  public void Our_ETag_Follows_The_Bytes()
  {
    Assert.Equal(new RewrittenContent("a"u8.ToArray()).ETag, new RewrittenContent("a"u8.ToArray()).ETag);
    Assert.NotEqual(new RewrittenContent("a"u8.ToArray()).ETag, new RewrittenContent("b"u8.ToArray()).ETag);
    Assert.True(EntityTagHeaderValue.TryParse(new RewrittenContent("a"u8.ToArray()).ETag, out _));
  }

  [Fact]
  public void Variant_Never_Collides_With_An_Accept_Variant()
  {
    // An Accept variant is a list of media types, which never starts with a newline.
    Assert.StartsWith("\n", ourSpace.FoldIntoVariant(null));
    Assert.Contains("https://cache-redirector.jetbrains.com/nuget-build/", ourSpace.FoldIntoVariant("application/json"));
  }

  private static HttpResponseMessage Response(byte[] body, string mediaType, string? encoding = null)
  {
    var content = new StreamContent(new MemoryStream(body));
    content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    if (encoding != null) content.Headers.ContentEncoding.Add(encoding);
    return new HttpResponseMessage { Content = content };
  }

  private static byte[] Gzip(string text)
  {
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionMode.Compress))
      gzip.Write(Encoding.UTF8.GetBytes(text));
    return output.ToArray();
  }

  private static string Gunzip(byte[] bytes)
  {
    using var reader = new StreamReader(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress), Encoding.UTF8);
    return reader.ReadToEnd();
  }
}
