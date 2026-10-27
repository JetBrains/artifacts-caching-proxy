using System;
using System.Buffers;
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
/// <para>Applied to a body before it is stored, never on serve: a stored object over the S3 inline threshold
/// is served by the bucket and its bytes never pass through the proxy again.</para>
/// <para>Only a JSON string that starts with the root is touched (the match is anchored on its opening quote),
/// so a URL quoted inside a description stays as written. Both the plain and the <c>\/</c>-escaped spelling
/// are matched.</para>
/// </summary>
public sealed class UpstreamUrlRewrite
{
  // Registration documents run to a few hundred KB. A body over this is stored as the upstream sent it.
  public const int MaxBodyBytes = 16 * 1024 * 1024;

  private readonly byte[] myAnchor;
  private readonly (byte[] From, byte[] To)[] myReplacements;

  public UpstreamUrlRewrite(Uri upstreamRoot, Uri publicRoot)
  {
    UpstreamRoot = upstreamRoot;
    PublicRoot = publicRoot;

    var from = upstreamRoot.AbsoluteUri;
    var to = publicRoot.AbsoluteUri;
    myAnchor = Encoding.UTF8.GetBytes($"\"{upstreamRoot.Scheme}:");
    myReplacements =
    [
      (Encoding.UTF8.GetBytes($"\"{from}"), Encoding.UTF8.GetBytes($"\"{to}")),
      (Encoding.UTF8.GetBytes($"\"{from.Replace("/", "\\/")}"), Encoding.UTF8.GetBytes($"\"{to.Replace("/", "\\/")}")),
    ];
  }

  public Uri UpstreamRoot { get; }
  public Uri PublicRoot { get; }

  /// <summary>
  /// Folds this rewrite into a cache variant (see <see cref="RemoteProxy.GetCacheVariant"/>). A rewritten
  /// body depends on the public URL, and an entry stored before rewriting was enabled must not be kept alive
  /// by a 304. A leading newline cannot start an Accept-derived variant, so the two never collide.
  /// </summary>
  public string FoldIntoVariant(string? variant) => $"\nrewrite {PublicRoot.AbsoluteUri}\n{variant}";

  /// <summary>The rewritten body, or null when it holds no URL under the upstream root.</summary>
  public byte[]? Rewrite(ReadOnlySpan<byte> json)
  {
    ArrayBufferWriter<byte>? output = null;
    var rest = json;
    var scanned = 0;
    while (true)
    {
      var anchor = rest[scanned..].IndexOf(myAnchor);
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

      if (!matched) scanned = anchor + myAnchor.Length;
    }

    if (output == null) return null;
    output.Write(rest);
    return output.WrittenSpan.ToArray();
  }

  /// <summary>
  /// Replaces <paramref name="response"/>'s content with the rewritten body: a <see cref="RewrittenContent"/>
  /// when anything changed, the same bytes otherwise. Leaves anything that is not JSON, or larger than
  /// <see cref="MaxBodyBytes"/>, unread. A gzip body is decoded, rewritten and encoded again, since that is
  /// how the cache stores it.
  /// </summary>
  /// <param name="idleReadTimeout">Real time, as in <c>CachingProxy.CopyToTwoStreamsAsync</c>: the body is read
  /// here, before that loop would have bounded it.</param>
  public async Task ApplyAsync(HttpResponseMessage response, TimeSpan idleReadTimeout, CancellationToken cancellationToken)
  {
    var original = response.Content;
    if (!IsJson(original.Headers.ContentType?.MediaType) || original.Headers.ContentLength > MaxBodyBytes)
      return;

    var stream = await original.ReadAsStreamAsync(cancellationToken);
    var (raw, complete) = await ReadUpToAsync(stream, MaxBodyBytes, idleReadTimeout, cancellationToken);
    if (!complete)
    {
      // A chunked body past the cap: hand on what was read plus the rest, untouched.
      response.Content = CopyHeaders(original, new StreamContent(new PrefixedStream(raw, stream)), rewritten: false);
      return;
    }

    var gzip = original.Headers.ContentEncoding.Count == 1 &&
               string.Equals(original.Headers.ContentEncoding.First(), "gzip", StringComparison.OrdinalIgnoreCase);
    var rewritten = gzip ? RewriteGzip(raw) : Rewrite(raw);
    response.Content = rewritten != null
      ? CopyHeaders(original, new RewrittenContent(rewritten), rewritten: true)
      : CopyHeaders(original, new ByteArrayContent(raw), rewritten: false);
    original.Dispose();
  }

  // Null for a body that does not decode, decodes past the cap, or needs no change: each is stored as sent.
  private byte[]? RewriteGzip(byte[] raw)
  {
    byte[] decoded;
    try
    {
      using var input = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
      using var output = new MemoryStream();
      var buffer = new byte[81920];
      int read;
      while ((read = input.Read(buffer)) > 0)
      {
        if (output.Length + read > MaxBodyBytes) return null;
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

  private static bool IsJson(string? mediaType) =>
    mediaType != null &&
    (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
     mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase) ||
     mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

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
