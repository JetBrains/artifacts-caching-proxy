using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JetBrains.CachingProxy;

/// <summary>
/// MRI-4855: swaps a prefix's upstream root for its public URL in a JSON body, so the links an upstream
/// document carries (NuGet registration's <c>packageContent</c>, <c>@id</c>, dependency <c>registration</c>)
/// lead back through the public route instead of straight to the upstream.
/// <para>MRI-5318: also an HTML body, and the roots of other prefixes (<see cref="OtherRoots"/>): a PyPI simple
/// index on pypi.org links its files on files.pythonhosted.org, which is another prefix.</para>
/// <para>Applied to a body before it is stored, never on serve: a stored object over the S3 inline threshold
/// is served by the bucket and its bytes never pass through the proxy again.</para>
/// <para>Only a string that starts with a root is touched (the match is anchored on its opening double quote:
/// a JSON string or an HTML attribute value), so a URL quoted inside a description stays as written. Both the
/// plain and the <c>\/</c>-escaped spelling are matched. Whatever follows the root, a <c>#sha256=</c> fragment
/// included, is kept byte for byte.</para>
/// </summary>
public sealed class UpstreamUrlRewrite
{
  // Registration documents run to a few hundred KB. A body over this is stored as the upstream sent it.
  public const int MaxBodyBytes = 16 * 1024 * 1024;

  // Every root is http(s).
  private static readonly byte[] ourAnchor = "\"http"u8.ToArray();
  private readonly (byte[] From, byte[] To)[] myReplacements;

  /// <param name="otherRoots">Other prefixes' upstream and public roots. Ones under <paramref name="upstreamRoot"/>
  /// are dropped, so a link the prefix itself serves keeps its alias; the rest match longest first.</param>
  public UpstreamUrlRewrite(Uri upstreamRoot, Uri publicRoot, IEnumerable<(Uri Upstream, Uri Public)>? otherRoots = null)
  {
    UpstreamRoot = upstreamRoot;
    PublicRoot = publicRoot;
    OtherRoots =
    [
      .. (otherRoots ?? [])
        .Where(root => !root.Upstream.AbsoluteUri.StartsWith(upstreamRoot.AbsoluteUri, StringComparison.Ordinal))
        .OrderByDescending(root => root.Upstream.AbsoluteUri.Length)
        .ThenBy(root => root.Upstream.AbsoluteUri, StringComparer.Ordinal)
    ];

    myReplacements =
    [
      .. new[] { (Upstream: upstreamRoot, Public: publicRoot) }.Concat(OtherRoots).SelectMany(static root =>
      {
        var from = root.Upstream.AbsoluteUri;
        var to = root.Public.AbsoluteUri;
        return new[]
        {
          (Encoding.UTF8.GetBytes($"\"{from}"), Encoding.UTF8.GetBytes($"\"{to}")),
          (Encoding.UTF8.GetBytes($"\"{from.Replace("/", "\\/")}"), Encoding.UTF8.GetBytes($"\"{to.Replace("/", "\\/")}")),
        };
      })
    ];
  }

  public Uri UpstreamRoot { get; }
  public Uri PublicRoot { get; }
  public IReadOnlyList<(Uri Upstream, Uri Public)> OtherRoots { get; }

  // One per host a signed request has named (see RedirectSignatureAuthenticationHandler.PublicHostQueryParam),
  // which is one per redirector deployment.
  private readonly ConcurrentDictionary<string, UpstreamUrlRewrite> myOnHost = new(StringComparer.Ordinal);

  /// <summary>
  /// This rewrite with every public root moved to <paramref name="host"/>: the same path on the redirector
  /// deployment the request came through. <see cref="FoldIntoVariant"/> follows, so each host has its own entry.
  /// </summary>
  public UpstreamUrlRewrite OnHost(string host) =>
    string.Equals(host, PublicRoot.Host, StringComparison.OrdinalIgnoreCase)
      ? this
      : myOnHost.GetOrAdd(host, static (h, self) =>
        new UpstreamUrlRewrite(self.UpstreamRoot, Move(self.PublicRoot, h),
          self.OtherRoots.Select(root => (root.Upstream, Move(root.Public, h)))), this);

  private static Uri Move(Uri uri, string host) => new UriBuilder(uri) { Host = host }.Uri;

  /// <summary>
  /// Folds this rewrite into a cache variant (see <see cref="RemoteProxy.GetCacheVariant"/>). A rewritten
  /// body depends on every root it maps, and an entry stored before a mapping existed must not be kept alive
  /// by a 304. A leading newline cannot start an Accept-derived variant, so the two never collide.
  /// </summary>
  public string FoldIntoVariant(string? variant) =>
    $"\nrewrite {PublicRoot.AbsoluteUri}\n" +
    string.Concat(OtherRoots.Select(static root => $"rewrite {root.Upstream.AbsoluteUri} {root.Public.AbsoluteUri}\n")) +
    variant;

  /// <summary>The rewritten body, or null when it holds no URL under any root.</summary>
  public byte[]? Rewrite(ReadOnlySpan<byte> json)
  {
    ArrayBufferWriter<byte>? output = null;
    var rest = json;
    var scanned = 0;
    while (true)
    {
      var anchor = rest[scanned..].IndexOf(ourAnchor);
      if (anchor < 0) break;
      anchor += scanned;

      var matched = false;
      foreach (var (from, to) in myReplacements)
      {
        if (!rest[anchor..].StartsWith(from)) continue;
        output ??= new ArrayBufferWriter<byte>(json.Length + 4096);
        output.Write(rest[..anchor]);
        output.Write(to);
        rest = rest[(anchor + from.Length)..];
        scanned = 0;
        matched = true;
        break;
      }

      if (!matched) scanned = anchor + ourAnchor.Length;
    }

    if (output == null) return null;
    output.Write(rest);
    return output.WrittenSpan.ToArray();
  }

  /// <summary>
  /// Replaces <paramref name="response"/>'s content with the rewritten body: a <see cref="RewrittenContent"/>
  /// when anything changed, the same bytes otherwise. Leaves anything that is not JSON or HTML, or larger than
  /// <see cref="MaxBodyBytes"/>, unread. A gzip body is decoded, rewritten and encoded again, since that is
  /// how the cache stores it.
  /// </summary>
  /// <param name="idleReadTimeout">Real time, as in <c>CachingProxy.CopyToTwoStreamsAsync</c>: the body is read
  /// here, before that loop would have bounded it.</param>
  /// <returns>False for a body past <see cref="MaxBodyBytes"/>: stored as sent, so its links still lead to the
  /// upstream. The caller warns about it.</returns>
  public async Task<bool> ApplyAsync(HttpResponseMessage response, TimeSpan idleReadTimeout, CancellationToken cancellationToken)
  {
    var original = response.Content;
    if (!IsRewritable(original.Headers.ContentType?.MediaType)) return true;
    if (original.Headers.ContentLength > MaxBodyBytes) return false;

    var stream = await original.ReadAsStreamAsync(cancellationToken);
    var (raw, complete) = await ReadUpToAsync(stream, MaxBodyBytes, idleReadTimeout, cancellationToken);
    if (!complete)
    {
      // A chunked body past the cap: hand on what was read plus the rest, untouched.
      response.Content = CopyHeaders(original, new StreamContent(new PrefixedStream(raw, stream)), rewritten: false);
      return false;
    }

    var gzip = original.Headers.ContentEncoding.Count == 1 &&
               string.Equals(original.Headers.ContentEncoding.First(), "gzip", StringComparison.OrdinalIgnoreCase);
    var tooLarge = false;
    var rewritten = gzip ? RewriteGzip(raw, out tooLarge) : Rewrite(raw);
    response.Content = rewritten != null
      ? CopyHeaders(original, new RewrittenContent(rewritten), rewritten: true)
      : CopyHeaders(original, new ByteArrayContent(raw), rewritten: false);
    original.Dispose();
    return !tooLarge;
  }

  // Null for a body that does not decode, decodes past the cap, or needs no change: each is stored as sent.
  private byte[]? RewriteGzip(byte[] raw, out bool tooLarge)
  {
    tooLarge = false;
    byte[] decoded;
    try
    {
      using var input = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
      using var output = new MemoryStream();
      var buffer = new byte[81920];
      int read;
      while ((read = input.Read(buffer)) > 0)
      {
        if (output.Length + read > MaxBodyBytes)
        {
          tooLarge = true;
          return null;
        }
        output.Write(buffer, 0, read);
      }
      decoded = output.ToArray();
    }
    catch (InvalidDataException)
    {
      return null;
    }

    if (Rewrite(decoded) is not { } rewritten) return null;

    using var encoded = new MemoryStream();
    using (var gzip = new GZipStream(encoded, CompressionLevel.Optimal, leaveOpen: true))
      gzip.Write(rewritten);
    return encoded.ToArray();
  }

  // HTML for a PEP 503 simple index: text/html, or PEP 691's application/vnd.pypi.simple.v1+html.
  private static bool IsRewritable(string? mediaType) =>
    mediaType != null &&
    (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
     mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase) ||
     mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
     mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
     mediaType.EndsWith("+html", StringComparison.OrdinalIgnoreCase));

  // Content-Length comes from the new content itself. Content-MD5 (Azure blob storage, i.e. nuget.org, sends
  // one) describes the upstream's bytes, not ours.
  private static HttpContent CopyHeaders(HttpContent from, HttpContent to, bool rewritten)
  {
    foreach (var (name, values) in from.Headers)
    {
      if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
      if (rewritten && name.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase)) continue;
      to.Headers.TryAddWithoutValidation(name, values);
    }

    return to;
  }

  private static async Task<(byte[] Bytes, bool Complete)> ReadUpToAsync(Stream source, int limit,
    TimeSpan idleReadTimeout, CancellationToken cancellationToken)
  {
    using var buffer = new MemoryStream();
    var chunk = new byte[81920];
    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    while (true)
    {
      int read;
      idleCts.CancelAfter(idleReadTimeout);
      try
      {
        // Never past limit + 1: one byte over is enough to know the body does not fit.
        read = await source.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1 - buffer.Length)), idleCts.Token);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        throw new TimeoutException($"Upstream sent no data for {idleReadTimeout.TotalSeconds:0}s after {buffer.Length} bytes");
      }
      finally
      {
        idleCts.CancelAfter(Timeout.InfiniteTimeSpan);
      }

      if (read == 0) return (buffer.ToArray(), true);
      buffer.Write(chunk, 0, read);
      if (buffer.Length > limit) return (buffer.ToArray(), false);
    }
  }

  // The bytes already read, then the rest of the upstream body.
  private sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
  {
    private int myPosition;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
      if (myPosition >= prefix.Length) return rest.Read(buffer);
      var length = Math.Min(buffer.Length, prefix.Length - myPosition);
      prefix.AsSpan(myPosition, length).CopyTo(buffer);
      myPosition += length;
      return length;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
      if (myPosition >= prefix.Length) return await rest.ReadAsync(buffer, cancellationToken);
      return Read(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
      ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
      if (disposing) rest.Dispose();
      base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}

/// <summary>
/// A body <see cref="UpstreamUrlRewrite"/> changed. Carries our own validator for it: the upstream's ETag
/// and Last-Modified describe other bytes, so neither may reach a client for this body (a client or edge
/// holding a pre-rewrite copy would get a 304 and keep it). Both stay recorded for upstream revalidation.
/// </summary>
public sealed class RewrittenContent(byte[] body) : ByteArrayContent(body)
{
  public string ETag { get; } = $"\"rw-{Convert.ToHexStringLower(SHA256.HashData(body))[..32]}\"";
}
