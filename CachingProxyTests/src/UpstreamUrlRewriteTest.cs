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

  // MRI-5318: a pypi.org simple index links its files on files.pythonhosted.org, another prefix.
  private static readonly UpstreamUrlRewrite ourPypi = new(
    new Uri("https://pypi.org/simple/"),
    new Uri("https://cache-redirector.jetbrains.com/pypi.org/simple/"),
    [
      (new Uri("https://files.pythonhosted.org/packages/"), new Uri("https://cache-redirector.jetbrains.com/files.pythonhosted.org/packages/")),
      (new Uri("https://files.pythonhosted.org/"), new Uri("https://cache-redirector.jetbrains.com/fph/")),
      // Under our own root: our own alias wins.
      (new Uri("https://pypi.org/simple/idna/"), new Uri("https://cache-redirector.jetbrains.com/idna/")),
    ]);

  private static string? RewritePypi(string body) =>
    ourPypi.Rewrite(Encoding.UTF8.GetBytes(body)) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

  [Fact]
  public void Simple_Html_Links_Go_To_The_Prefix_Of_Their_Host_With_The_Fragment_Kept()
  {
    Assert.Equal(
      """<a href="https://cache-redirector.jetbrains.com/files.pythonhosted.org/packages/0e/idna-3.10-py3-none-any.whl#sha256=946d" data-requires-python="&gt;=3.6">idna-3.10-py3-none-any.whl</a>""",
      RewritePypi("""<a href="https://files.pythonhosted.org/packages/0e/idna-3.10-py3-none-any.whl#sha256=946d" data-requires-python="&gt;=3.6">idna-3.10-py3-none-any.whl</a>"""));
  }

  [Fact]
  public void Simple_Json_Url_Is_Rewritten()
  {
    Assert.Equal(
      """{"files":[{"filename":"idna-3.10.tar.gz","url":"https://cache-redirector.jetbrains.com/files.pythonhosted.org/packages/9f/idna-3.10.tar.gz","hashes":{"sha256":"12f6"}}]}""",
      RewritePypi("""{"files":[{"filename":"idna-3.10.tar.gz","url":"https://files.pythonhosted.org/packages/9f/idna-3.10.tar.gz","hashes":{"sha256":"12f6"}}]}"""));
  }

  [Fact]
  public void Space_Percent_Encoded_Fragment_Is_Kept_Byte_For_Byte()
  {
    var space = new UpstreamUrlRewrite(
      new Uri("https://packages.jetbrains.team/pypi/p/"),
      new Uri("https://cache-redirector.jetbrains.com/pypi/p/"));
    var html = """<a href="https://packages.jetbrains.team/pypi/p/ij/pypi-mirror/download/idna/0.2/idna-0.2.tar.gz#sha256%3D0f4b">idna-0.2.tar.gz</a>""";

    Assert.Equal(
      """<a href="https://cache-redirector.jetbrains.com/pypi/p/ij/pypi-mirror/download/idna/0.2/idna-0.2.tar.gz#sha256%3D0f4b">idna-0.2.tar.gz</a>""",
      Encoding.UTF8.GetString(space.Rewrite(Encoding.UTF8.GetBytes(html))!));
  }

  [Theory]
  // The longest root wins.
  [InlineData("\"https://files.pythonhosted.org/other/x\"", "\"https://cache-redirector.jetbrains.com/fph/other/x\"")]
  // Our own root wins over another prefix nested in it.
  [InlineData("\"https://pypi.org/simple/idna/\"", "\"https://cache-redirector.jetbrains.com/pypi.org/simple/idna/\"")]
  public void Roots_Match_Own_First_Then_Longest(string body, string expected) =>
    Assert.Equal(expected, RewritePypi(body));

  [Fact]
  public void Unquoted_Or_Foreign_Links_Are_Left_Alone()
  {
    Assert.Null(RewritePypi("<p>see https://files.pythonhosted.org/packages/x</p>"));
    Assert.Null(RewritePypi("""<a href="https://example.org/packages/x.whl">x</a>"""));
  }

  [Fact]
  public void On_Host_Moves_Every_Root()
  {
    var moved = ourPypi.OnHost("sandbox.example");
    Assert.Equal(
      "\"https://sandbox.example/files.pythonhosted.org/packages/x.whl\"",
      Encoding.UTF8.GetString(moved.Rewrite("\"https://files.pythonhosted.org/packages/x.whl\""u8)!));
    Assert.DoesNotContain("cache-redirector.jetbrains.com", moved.FoldIntoVariant(null));
  }

  [Fact]
  public void Variant_Follows_The_Other_Roots()
  {
    // Without other roots the variant is what MRI-4855 shipped, so its stored entries stay valid.
    Assert.Equal("\nrewrite https://cache-redirector.jetbrains.com/nuget-build/\naccept", ourSpace.FoldIntoVariant("accept"));
    Assert.Contains("rewrite https://files.pythonhosted.org/packages/ https://cache-redirector.jetbrains.com/files.pythonhosted.org/packages/\n",
      ourPypi.FoldIntoVariant(null));
  }

  [Theory]
  [InlineData("text/html")]
  [InlineData("application/vnd.pypi.simple.v1+html")]
  [InlineData("application/vnd.pypi.simple.v1+json")]
  public async Task Simple_Index_Media_Types_Are_Rewritten(string mediaType)
  {
    using var response = Response("\"https://files.pythonhosted.org/packages/x.whl\""u8.ToArray(), mediaType);

    await ourPypi.ApplyAsync(response, Timeout.InfiniteTimeSpan, CancellationToken.None);

    Assert.IsType<RewrittenContent>(response.Content);
  }

  [Fact]
  public async Task Body_Past_The_Cap_Is_Reported()
  {
    // Stored as sent, so its links still lead to the upstream: the caller has to hear about it.
    using var declared = Response([], "application/json");
    declared.Content.Headers.ContentLength = UpstreamUrlRewrite.MaxBodyBytes + 1;
    Assert.False(await ourSpace.ApplyAsync(declared, Timeout.InfiniteTimeSpan, CancellationToken.None));

    // Small on the wire, past the cap once decoded.
    using var gzipped = Response(Gzip(new string(' ', UpstreamUrlRewrite.MaxBodyBytes + 1)), "application/json", "gzip");
    Assert.False(await ourSpace.ApplyAsync(gzipped, Timeout.InfiniteTimeSpan, CancellationToken.None));
  }

  [Theory]
  [InlineData("application/json")]
  // Never rewritten whatever its size, so nothing is lost.
  [InlineData("application/octet-stream")]
  public async Task Body_Within_The_Cap_Or_Not_Rewritable_Is_Not_Reported(string mediaType)
  {
    using var response = Response("{}"u8.ToArray(), mediaType);
    if (mediaType != "application/json") response.Content.Headers.ContentLength = UpstreamUrlRewrite.MaxBodyBytes + 1;
    Assert.True(await ourSpace.ApplyAsync(response, Timeout.InfiniteTimeSpan, CancellationToken.None));
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
