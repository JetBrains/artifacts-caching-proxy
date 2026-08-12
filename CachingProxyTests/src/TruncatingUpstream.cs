using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace JetBrains.CachingProxy.Tests;

// An upstream that answers every request with a head promising more body than it sends, then ends the
// connection: the truncated transfer a real CDN produces when it dies mid-download.
//
// Raw sockets rather than a route on UpstreamTestServer, because Kestrel cannot be made to end a body early
// without resetting the connection, and a reset discards whatever is still in flight - including the head,
// which turns the test into a head-phase connection failure instead, at random.
public sealed class TruncatingUpstream : IDisposable
{
  // Bytes the head declares versus bytes actually sent, so the caller can assert on both ends of the gap.
  public const int DeclaredLength = 4096;
  public const int SentLength = 1024;

  public Uri Url { get; }

  private readonly TcpListener myListener;

  public TruncatingUpstream()
  {
    myListener = new TcpListener(IPAddress.Loopback, 0);
    myListener.Start();
    Url = new Uri($"http://127.0.0.1:{((IPEndPoint)myListener.LocalEndpoint).Port}/");
    _ = AcceptAsync();
  }

  private async Task AcceptAsync()
  {
    try
    {
      while (true)
      {
        using var client = await myListener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();

        // Drain the request head. Leaving it unread would make the close below a reset rather than an
        // orderly shutdown, which is the very failure this server exists to avoid.
        var request = new byte[8192];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n"))
        {
          var read = await stream.ReadAsync(request);
          if (read == 0) break;
          received.Append(Encoding.ASCII.GetString(request, 0, read));
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
          "HTTP/1.1 200 OK\r\n" +
          "Content-Type: application/octet-stream\r\n" +
          $"Content-Length: {DeclaredLength}\r\n" +
          "Connection: close\r\n\r\n"));
        await stream.WriteAsync(new byte[SentLength]);
        await stream.FlushAsync();

        // FIN after a partial body: the peer's next read reports the end of a response that is short of its
        // Content-Length, which is exactly what an interrupted download looks like.
        client.Client.Shutdown(SocketShutdown.Send);
      }
    }
    catch (Exception)
    {
      // Disposed listener, or a client that left mid-exchange. Either way there is nothing left to serve.
    }
  }

  public void Dispose() => myListener.Dispose();
}
