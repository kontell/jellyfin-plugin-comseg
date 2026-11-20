using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Catchup;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class ImageFetcherTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // 127.0.0.2 is loopback on Linux too, so it stands in for an address the rule refuses.
    private static readonly IPAddress Allowed = IPAddress.Loopback;
    private static readonly IPAddress Refused = IPAddress.Parse("127.0.0.2");

    private static ImageFetcher Fetcher(TimeSpan? timeout = null, int maxBytes = 1000)
        => new(a => a.Equals(Allowed), timeout ?? TimeSpan.FromSeconds(10), maxBytes);

    private static byte[] Head(string status, string headers)
        => Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" + headers + "Connection: close\r\n\r\n");

    private static Func<Stream, CancellationToken, Task> Image(byte[] body, string type = "image/png")
        => async (stream, ct) =>
        {
            await stream.WriteAsync(Head("200 OK", "Content-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\n"), ct);
            await stream.WriteAsync(body, ct);
        };

    [Fact]
    public async Task AnImageFromAnAllowedAddressIsFetched()
    {
        await using var server = new RawServer(Allowed, Image(Png));
        using var fetcher = Fetcher();
        var (image, problem) = await fetcher.FetchAsync(server.Url("/p.png"), CancellationToken.None);
        Assert.Null(problem);
        Assert.Equal(".png", image!.Extension);
        Assert.Equal(Png, image.Bytes);
    }

    [Fact]
    public async Task AnAddressTheRuleRefusesIsNeverConnectedTo()
    {
        await using var server = new RawServer(Refused, Image(Png));
        using var fetcher = Fetcher();
        var (image, problem) = await fetcher.FetchAsync(server.Url("/p.png"), CancellationToken.None);
        Assert.Null(image);
        Assert.Contains("not an address images may be fetched from", problem, StringComparison.Ordinal);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task ARedirectToARefusedAddressIsNotFollowed()
    {
        await using var inside = new RawServer(Refused, Image(Png));
        await using var outside = new RawServer(Allowed, (stream, ct) => stream.WriteAsync(Head("302 Found", "Location: " + inside.Url("/p.png") + "\r\nContent-Length: 0\r\n"), ct).AsTask());
        using var fetcher = Fetcher();
        var (image, problem) = await fetcher.FetchAsync(outside.Url("/p.png"), CancellationToken.None);
        Assert.Null(image);
        Assert.Contains("not an address images may be fetched from", problem, StringComparison.Ordinal);
        Assert.Equal(1, outside.Connections);
        Assert.Equal(0, inside.Connections);
    }

    [Fact]
    public async Task ABodyThatStallsAfterTheHeadersTimesOut()
    {
        await using var server = new RawServer(Allowed, async (stream, ct) =>
        {
            await stream.WriteAsync(Head("200 OK", "Content-Type: image/png\r\nContent-Length: 500\r\n"), ct);
            await stream.WriteAsync(Png, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        using var fetcher = Fetcher(TimeSpan.FromSeconds(1));
        var clock = Stopwatch.StartNew();
        var (image, problem) = await fetcher.FetchAsync(server.Url("/p.png"), CancellationToken.None);
        Assert.Null(image);
        Assert.Equal("timed out", problem);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task AnImageLargerThanTheLimitIsRefused()
    {
        // Said in Content-Length: refused before the body is read.
        await using var announced = new RawServer(Allowed, Image(new byte[1001]));
        using var fetcher = Fetcher();
        Assert.Equal("too large", (await fetcher.FetchAsync(announced.Url("/p.png"), CancellationToken.None)).Problem);

        // Not said: refused once the body passes the limit.
        await using var unannounced = new RawServer(Allowed, async (stream, ct) =>
        {
            await stream.WriteAsync(Head("200 OK", "Content-Type: image/png\r\n"), ct);
            await stream.WriteAsync(new byte[1001], ct);
        });
        Assert.Equal("too large", (await fetcher.FetchAsync(unannounced.Url("/p.png"), CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task WhatIsNotAnImageIsRefused()
    {
        await using var server = new RawServer(Allowed, Image(Encoding.ASCII.GetBytes("<html></html>"), "text/html"));
        using var fetcher = Fetcher();
        Assert.Equal("not an image (text/html)", (await fetcher.FetchAsync(server.Url("/p.png"), CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task TheCallersCancellationIsPassedOn()
    {
        await using var server = new RawServer(Allowed, async (stream, ct) =>
        {
            await stream.WriteAsync(Head("200 OK", "Content-Type: image/png\r\nContent-Length: 500\r\n"), ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        using var fetcher = Fetcher();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(server.Url("/p.png"), cancel.Token));
    }

    /// <summary>A one-response-per-connection HTTP server whose answer a test writes byte by byte.</summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<Stream, CancellationToken, Task> _respond;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _connections;

        public RawServer(IPAddress address, Func<Stream, CancellationToken, Task> respond)
        {
            _respond = respond;
            _listener = new TcpListener(address, 0);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public int Connections => Volatile.Read(ref _connections);

        public Uri Url(string path)
        {
            var endPoint = (IPEndPoint)_listener.LocalEndpoint;
            return new Uri("http://" + endPoint.Address + ":" + endPoint.Port + path);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            _stop.Dispose();
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => AnswerAsync(client));
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var seen = new StringBuilder();
                    var buffer = new byte[1024];
                    while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                        {
                            return;
                        }

                        seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    await _respond(stream, _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
    }
}
