using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Comseg.Catchup;

/// <summary>An image fetched for a poster.</summary>
/// <param name="Bytes">The image.</param>
/// <param name="Extension">The file extension its type calls for, with the dot.</param>
public sealed record FetchedImage(byte[] Bytes, string Extension);

/// <summary>
/// Fetches a programme's image for its poster. The URL comes from a client,
/// so the fetcher connects only to addresses a rule allows (in the plugin,
/// public ones). The rule is applied as each connection is made, so a
/// redirect, or a name that resolves elsewhere later, is held to it too, and
/// no proxy is used, which would hide the address. One deadline covers the
/// whole fetch, body included, and the body is read no further than a limit.
/// </summary>
public sealed class ImageFetcher : IDisposable
{
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    private readonly int _maxBytes;

    /// <summary>Initializes a new instance of the <see cref="ImageFetcher"/> class.</summary>
    /// <param name="allowed">Which addresses may be connected to.</param>
    /// <param name="timeout">How long a whole fetch may take.</param>
    /// <param name="maxBytes">The largest image accepted.</param>
    public ImageFetcher(Func<IPAddress, bool> allowed, TimeSpan timeout, int maxBytes)
    {
        _timeout = timeout;
        _maxBytes = maxBytes;
        _client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = timeout,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(allowed, context, cancellationToken),
        })
        {
            // The deadline in FetchAsync covers the body, which this would not.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>The plugin's fetcher: public addresses only, 30 seconds, 10 MiB.</summary>
    /// <returns>The fetcher.</returns>
    public static ImageFetcher PublicOnly() => new(PublicAddress.IsPublic, TimeSpan.FromSeconds(30), 10 << 20);

    /// <summary>Fetches an image.</summary>
    /// <param name="image">The image's URL.</param>
    /// <param name="cancellationToken">Cancellation, which is passed on rather than reported as a problem.</param>
    /// <returns>The image, or why it cannot be used.</returns>
    public async Task<(FetchedImage? Image, string? Problem)> FetchAsync(Uri image, CancellationToken cancellationToken)
    {
        if (image.Scheme != Uri.UriSchemeHttp && image.Scheme != Uri.UriSchemeHttps)
        {
            return (null, "not an http or https URL");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            using var response = await _client.GetAsync(image, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, "HTTP " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var extension = type switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => null,
            };
            if (extension is null)
            {
                return (null, "not an image (" + (type.Length == 0 ? "no type" : type) + ")");
            }

            if (response.Content.Headers.ContentLength > _maxBytes)
            {
                return (null, "too large");
            }

            var bytes = await ReadAtMostAsync(response.Content, _maxBytes, deadline.Token).ConfigureAwait(false);
            return bytes switch
            {
                null => (null, "too large"),
                { Length: 0 } => (null, "empty"),
                _ => (new FetchedImage(bytes, extension), null),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>The body, or null when it is longer than <paramref name="limit"/>; read no further than that.</summary>
    private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var body = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > limit)
                {
                    return null;
                }

                body.Write(chunk, 0, read);
            }

            return body.ToArray();
        }
    }

    /// <summary>Connects to an allowed address of the host, and to nothing else.</summary>
    private static async ValueTask<Stream> ConnectAsync(Func<IPAddress, bool> allowed, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(allowed)
            ?? throw new HttpRequestException(context.DnsEndPoint.Host + " is not an address images may be fetched from");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
