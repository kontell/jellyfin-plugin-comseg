using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Catchup;

/// <summary>
/// The playlists of Jellyfin's M3U tuners, for the catchup queue, which finds a
/// channel's live URL in them, and for the channel profiles, which can go by a
/// channel's playlist groups. A read is kept for five minutes: a recording that
/// starts asks once, and a catchup request twice.
/// </summary>
public sealed class TunerPlaylists
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(5);
    private static readonly Regex Url = new(@"[a-z]+://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IConfigurationManager _config;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TunerPlaylists> _logger;
    private readonly SemaphoreSlim _reads = new(1, 1);
    private Read? _last;

    /// <summary>Initializes a new instance of the <see cref="TunerPlaylists"/> class.</summary>
    /// <param name="config">The server configuration, for the tuners.</param>
    /// <param name="http">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public TunerPlaylists(IConfigurationManager config, IHttpClientFactory http, ILogger<TunerPlaylists> logger)
    {
        _config = config;
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// A channel's entry in the first M3U tuner's playlist that has it (see
    /// <see cref="Archive.Find"/>), or why there is none.
    /// </summary>
    /// <param name="name">The channel's name.</param>
    /// <param name="number">The channel's number, when known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entry, or the problem.</returns>
    public async Task<(PlaylistEntry? Entry, string? Problem)> FindAsync(string? name, string? number, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.Tuners == 0)
        {
            return (null, "Jellyfin has no M3U tuner");
        }

        foreach (var playlist in read.Playlists)
        {
            if (Archive.Find(playlist, name, number) is { } entry)
            {
                return (entry, null);
            }
        }

        return (null, "no tuner playlist has the channel " + name + (string.IsNullOrWhiteSpace(number) ? string.Empty : " (" + number + ")"));
    }

    /// <summary>A channel's playlist groups, or null when no tuner playlist could be read that has it.</summary>
    /// <param name="name">The channel's name in Jellyfin.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The groups, possibly none.</returns>
    public async Task<IReadOnlyList<string>?> GroupsAsync(string? name, CancellationToken cancellationToken)
        => (await FindAsync(name, null, cancellationToken).ConfigureAwait(false)).Entry?.Groups;

    private async Task<Read> ReadAsync(CancellationToken cancellationToken)
    {
        await _reads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_last is { } last && DateTime.UtcNow - last.AtUtc < Keep)
            {
                return last;
            }

            var tuners = _config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts
                .Where(t => string.Equals(t.Type, "m3u", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(t.Url))
                .ToList();
            var playlists = new List<IReadOnlyList<PlaylistEntry>>();
            var failed = false;
            foreach (var tuner in tuners)
            {
                try
                {
                    var text = tuner.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? await _http.CreateClient(NamedClient.Default).GetStringAsync(new Uri(tuner.Url), cancellationToken).ConfigureAwait(false)
                        : await File.ReadAllTextAsync(tuner.Url, cancellationToken).ConfigureAwait(false);
                    playlists.Add(Archive.ParsePlaylist(text));
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or UriFormatException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    failed = true;
                    _logger.LogWarning("Comseg: could not read the playlist of tuner {Tuner}: {Problem}", tuner.FriendlyName ?? tuner.Id, Url.Replace(ex.Message, "<url>"));
                }
            }

            // A tuner that could not be read is asked again next time.
            var read = new Read(tuners.Count, playlists, DateTime.UtcNow);
            _last = failed ? null : read;
            return read;
        }
        finally
        {
            _reads.Release();
        }
    }

    private sealed record Read(int Tuners, IReadOnlyList<IReadOnlyList<PlaylistEntry>> Playlists, DateTime AtUtc);
}
