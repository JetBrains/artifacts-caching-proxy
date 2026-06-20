using System.Collections.Generic;
using System.Net;
using MemoryPack;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy;

/// <summary>
/// MemoryPack-serializable surrogate for <see cref="CachedResponse"/>. Headers are flattened to a
/// plain array of string/string[] pairs because the live <see cref="IHeaderDictionary"/> (an
/// ASP.NET Core interface backed by <see cref="StringValues"/>) is not directly serializable.
/// </summary>
[MemoryPackable]
public sealed partial record CachedResponseSurrogate(
  int StatusCode,
  KeyValuePair<string, string?[]>[] Headers,
  byte[]? Body);

/// <summary>
/// Custom MemoryPack formatter that maps <see cref="CachedResponse"/> to/from
/// <see cref="CachedResponseSurrogate"/>, so the L2 (Redis) distributed cache can store and replay
/// cached responses without changing the <see cref="CachedResponse"/> public shape.
/// </summary>
public sealed class CachedResponseFormatter : MemoryPackFormatter<CachedResponse>
{
  public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref CachedResponse? value)
  {
    if (value is null)
    {
      writer.WriteValue<CachedResponseSurrogate>(null);
      return;
    }

    var headers = new KeyValuePair<string, string?[]>[value.Headers.Count];
    var i = 0;
    foreach (var (key, val) in value.Headers)
      headers[i++] = new KeyValuePair<string, string?[]>(key, val.ToArray());

    writer.WriteValue(new CachedResponseSurrogate((int)value.StatusCode, headers, value.Body));
  }

  public override void Deserialize(ref MemoryPackReader reader, scoped ref CachedResponse? value)
  {
    var surrogate = reader.ReadValue<CachedResponseSurrogate>();
    if (surrogate is null)
    {
      value = null;
      return;
    }

    var headers = new HeaderDictionary(surrogate.Headers.Length);
    foreach (var (key, vals) in surrogate.Headers)
      headers[key] = new StringValues(vals);

    value = new CachedResponse((HttpStatusCode)surrogate.StatusCode, headers, surrogate.Body);
  }

  /// <summary>
  /// Registers this formatter with MemoryPack's global provider. Safe to call more than once;
  /// repeated registrations simply overwrite the previous one with an equivalent formatter.
  /// </summary>
  public static void Register() => MemoryPackFormatterProvider.Register(new CachedResponseFormatter());
}
