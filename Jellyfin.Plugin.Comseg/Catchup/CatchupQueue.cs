using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Catchup;

/// <summary>
/// Saves programmes from the IPTV provider's catchup archive into the
/// recording folder, one at a time, and runs the full pass over each.
///
/// The account behind the tuner allows few connections and Jellyfin's live TV
/// and recordings use them, so a job waits while Jellyfin is recording, has a
/// recording due before the download would end, or is showing live TV, and
/// stops and waits again if one of those starts while it downloads. It cannot
/// see viewing that does not go through this server.
///
/// URLs carry the account's tokens and are never logged.
/// </summary>
public sealed class CatchupQueue : BackgroundService
{
    /// <summary>The longest window a job may ask for.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(12);

    private const int MaxImageBytes = 10 << 20;

    private static readonly TimeSpan BusyPoll = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BusyCheckWhileDownloading = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepEnded = TimeSpan.FromDays(1);
    private static readonly Regex Url = new(@"[a-z]+://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ffmpeg's HLS reader giving up on a segment: "Segment 1 of playlist 0 failed too many times, skipping".</summary>
    private static readonly Regex SkippedSegment = new(@"egment \d+ .*skipping", RegexOptions.Compiled);

    /// <summary>
    /// The archive read that may stall before ffmpeg gives up, in microseconds:
    /// a segment arrives in well under a second, so a minute is a dead connection.
    /// </summary>
    private static readonly string ReadTimeoutMicroseconds = ((long)TimeSpan.FromSeconds(60).TotalMicroseconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Fetches the client's programme image. The URL comes from a client, so it
    /// may only reach public addresses, checked when each connection is made
    /// (redirects included), and never through a proxy that would hide the address.
    /// </summary>
    private static readonly HttpClient Images = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectToPublicAddressAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly ILiveTvManager _liveTv;
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly IConfigurationManager _config;
    private readonly IMediaEncoder _encoder;
    private readonly IHttpClientFactory _http;
    private readonly RecordingPaths _paths;
    private readonly OnDemandDetection _detection;
    private readonly ILogger<CatchupQueue> _logger;
    private readonly Channel<CatchupJob> _queue = Channel.CreateUnbounded<CatchupJob>();
    private readonly ConcurrentDictionary<Guid, CatchupJob> _jobs = new();
    private readonly Lock _enqueue = new();

    /// <summary>Initializes a new instance of the <see cref="CatchupQueue"/> class.</summary>
    /// <param name="liveTv">The live TV manager, for the timers.</param>
    /// <param name="sessions">The session manager, for live TV being watched.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="providers">The provider manager, to refresh the new folder.</param>
    /// <param name="fileSystem">The file system, for valid file names.</param>
    /// <param name="config">The server configuration, for the tuners and the recording path.</param>
    /// <param name="encoder">The media encoder, for ffmpeg.</param>
    /// <param name="http">The HTTP client factory.</param>
    /// <param name="paths">The recording paths.</param>
    /// <param name="detection">The full pass.</param>
    /// <param name="logger">The logger.</param>
    public CatchupQueue(
        ILiveTvManager liveTv,
        ISessionManager sessions,
        ILibraryManager library,
        IProviderManager providers,
        IFileSystem fileSystem,
        IConfigurationManager config,
        IMediaEncoder encoder,
        IHttpClientFactory http,
        RecordingPaths paths,
        OnDemandDetection detection,
        ILogger<CatchupQueue> logger)
    {
        _liveTv = liveTv;
        _sessions = sessions;
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
        _config = config;
        _encoder = encoder;
        _http = http;
        _paths = paths;
        _detection = detection;
        _logger = logger;
    }

    /// <summary>The jobs, newest first.</summary>
    /// <returns>The jobs.</returns>
    public IReadOnlyList<CatchupJob> Snapshot()
    {
        foreach (var old in _jobs.Values.Where(j => j.Ended && DateTime.UtcNow - j.UpdatedUtc > KeepEnded).ToList())
        {
            _jobs.TryRemove(old.Id, out _);
        }

        return _jobs.Values.OrderByDescending(j => j.CreatedUtc).ToList();
    }

    /// <summary>Queues a programme.</summary>
    /// <param name="request">The programme.</param>
    /// <param name="requestedBy">Who asked.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The job, or why it was refused and the HTTP status that says so.</returns>
    public async Task<(CatchupJob? Job, string? Problem, int Status)> EnqueueAsync(CatchupRequest request, string requestedBy, CancellationToken cancellationToken)
    {
        var config = ComsegPlugin.Config;
        if (!config.EnableCatchup)
        {
            return (null, "catchup downloads are turned off in Comseg's settings", 503);
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return (null, "the programme has no title", 400);
        }

        if (RecordingFiles.FolderName(request.Title, _fileSystem.GetValidFilename) is null)
        {
            return (null, "the programme's title makes no usable folder name", 400);
        }

        request.StartUtc = DateTime.SpecifyKind(request.StartUtc, DateTimeKind.Utc);
        request.EndUtc = DateTime.SpecifyKind(request.EndUtc, DateTimeKind.Utc);
        var length = request.EndUtc - request.StartUtc;
        if (length <= TimeSpan.Zero || length > MaxLength)
        {
            return (null, "the programme must last between a second and 12 hours", 400);
        }

        if (request.EndUtc > DateTime.UtcNow)
        {
            return (null, "the programme has not finished yet", 400);
        }

        var (entry, problem) = await FindChannelAsync(request, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return (null, problem, 404);
        }

        if (entry.ArchiveDays is <= 0)
        {
            return (null, entry.Name + " has no catchup archive", 400);
        }

        if (entry.ArchiveDays is int days && request.StartUtc < DateTime.UtcNow.AddDays(-days))
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"{entry.Name} keeps {days} days of archive; the programme is older than that"), 400);
        }

        // Checked and added under one lock, so two identical requests at once make one job.
        CatchupJob job;
        lock (_enqueue)
        {
            var duplicate = _jobs.Values.FirstOrDefault(j => !j.Ended && j.Channel == entry.Name && j.Request.StartUtc == request.StartUtc && j.Request.EndUtc == request.EndUtc);
            if (duplicate is not null)
            {
                return (duplicate, "that programme is already queued", 409);
            }

            job = new CatchupJob(request, entry.Name, requestedBy);
            _jobs[job.Id] = job;

            // The channel is unbounded and never completed, so this always succeeds.
            _queue.Writer.TryWrite(job);
        }

        _logger.LogInformation(
            "Comseg: {User} queued {Title} on {Channel}, {Start:u} to {End:u}",
            requestedBy,
            request.Title,
            entry.Name,
            request.StartUtc,
            request.EndUtc);
        return (job, null, 202);
    }

    /// <summary>Cancels a job.</summary>
    /// <param name="id">The job.</param>
    /// <returns>False when there is no such job or it has ended.</returns>
    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.Ended)
        {
            return false;
        }

        job.Cancel.Cancel();
        if (job.State == CatchupState.Queued)
        {
            job.Set(CatchupState.Cancelled);
        }

        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (job.Ended)
            {
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancel.Token);
            try
            {
                await RunAsync(job, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
            {
                job.Set(CatchupState.Cancelled);
                _logger.LogInformation("Comseg: the catchup of {Title} was cancelled", job.Request.Title);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Set(CatchupState.Failed, "the server stopped");
                return;
            }
            catch (Exception ex)
            {
                job.Set(CatchupState.Failed, Url.Replace(ex.Message, "<url>"));
                _logger.LogError("Comseg: the catchup of {Title} failed: {Problem}", job.Request.Title, job.Message);
            }
        }
    }

    private async Task RunAsync(CatchupJob job, CancellationToken cancellationToken)
    {
        var config = ComsegPlugin.Config;
        var request = job.Request;
        var (entry, problem) = await FindChannelAsync(request, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            job.Set(CatchupState.Failed, problem);
            return;
        }

        var pad = Math.Clamp(config.CatchupPadSeconds, 0, 120);
        var length = (int)Math.Ceiling((request.EndUtc - request.StartUtc).TotalSeconds);
        var askStart = request.StartUtc.AddSeconds(-pad);
        var url = Archive.ArchiveUrl(entry.Url, new DateTimeOffset(askStart).ToUnixTimeSeconds(), length + pad)
            ?? throw new InvalidOperationException(entry.Name + "'s URL is not .../NAME.m3u8, so not a Flussonic-style archive");

        var root = DefaultRecordingRoot() ?? throw new InvalidOperationException("Jellyfin has no recording folder");
        var (folder, fileBase) = RecordingFiles.Paths(root, request.Title!, request.StartUtc, _fileSystem.GetValidFilename);
        var target = fileBase + ".ts";
        if (File.Exists(target))
        {
            job.Path = target;
            job.Set(CatchupState.Failed, "that programme is already saved");
            return;
        }

        var maxWait = DateTime.UtcNow.AddHours(Math.Clamp(config.CatchupMaxWaitHours, 1, 48));
        while (true)
        {
            await WaitForConnectionAsync(job, length, maxWait, cancellationToken).ConfigureAwait(false);
            job.Set(CatchupState.Downloading, null);
            var check = await CheckPlaylistAsync(url, askStart, request.StartUtc, length, cancellationToken).ConfigureAwait(false);
            if (check.Problems.Count > 0)
            {
                job.Set(CatchupState.Failed, string.Join("; ", check.Problems));
                _logger.LogWarning("Comseg: not downloading {Title}: {Problems}", request.Title, job.Message);
                return;
            }

            Directory.CreateDirectory(folder);
            var why = await DownloadAsync(job, url, target, length + pad, cancellationToken).ConfigureAwait(false);
            if (why is null)
            {
                break;
            }

            // Something needs the connection: give it up and wait again.
            job.Set(CatchupState.Waiting, why);
            _logger.LogInformation("Comseg: stopped downloading {Title} because {Why}; it will start again", request.Title, why);
        }

        job.Path = target;
        job.Percent = 100;
        job.Set(CatchupState.Finishing);
        var poster = await WritePosterAsync(folder, target, request, length, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(folder, "movie.nfo"), RecordingFiles.MovieNfo(request.Title!, request.Plot, request.StartUtc, poster), cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(fileBase + ".nfo", RecordingFiles.FileNfo(request.Title!, request.Plot, request.StartUtc), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Comseg: saved {Title} from the archive as {Path}", request.Title, target);

        var item = await WaitForItemAsync(target, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            job.Set(CatchupState.Done, "saved; the library has not picked it up yet");
            return;
        }

        job.ItemId = item.Id;
        if (!config.EnableCompletedDetection)
        {
            job.Set(CatchupState.Done, "saved");
            return;
        }

        job.Set(CatchupState.Analysing);
        var pass = _detection.TryRun(item, "catchup", cancellationToken);
        if (pass is null)
        {
            job.Set(CatchupState.Done, "saved; a pass over it was already running");
            return;
        }

        var breaks = await pass.ConfigureAwait(false);
        job.Set(CatchupState.Done, breaks switch
        {
            null => "saved; the Comskip pass produced nothing",
            0 => "saved; no commercial breaks found",
            1 => "saved with 1 commercial break",
            int n => string.Create(CultureInfo.InvariantCulture, $"saved with {n} commercial breaks"),
        });
    }

    private async Task<(PlaylistEntry? Entry, string? Problem)> FindChannelAsync(CatchupRequest request, CancellationToken cancellationToken)
    {
        var tuners = _config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts
            .Where(t => string.Equals(t.Type, "m3u", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(t.Url))
            .ToList();
        if (tuners.Count == 0)
        {
            return (null, "Jellyfin has no M3U tuner");
        }

        foreach (var tuner in tuners)
        {
            string text;
            try
            {
                text = tuner.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? await _http.CreateClient(NamedClient.Default).GetStringAsync(new Uri(tuner.Url), cancellationToken).ConfigureAwait(false)
                    : await File.ReadAllTextAsync(tuner.Url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Comseg: could not read the playlist of tuner {Tuner}: {Problem}", tuner.FriendlyName ?? tuner.Id, Url.Replace(ex.Message, "<url>"));
                continue;
            }

            var entry = Archive.Find(Archive.ParsePlaylist(text), request.ChannelName, request.ChannelNumber);
            if (entry is not null)
            {
                return (entry, null);
            }
        }

        return (null, string.Create(CultureInfo.InvariantCulture, $"no tuner playlist has the channel {request.ChannelName} ({request.ChannelNumber})"));
    }

    private string? DefaultRecordingRoot()
    {
        var configured = _config.GetConfiguration<LiveTvOptions>("livetv").RecordingPath;
        return string.IsNullOrWhiteSpace(configured) ? _paths.Roots(ComsegPlugin.Config).FirstOrDefault() : configured;
    }

    private async Task WaitForConnectionAsync(CatchupJob job, int length, DateTime giveUpUtc, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Downloads ran at 17-30x real time; 10x leaves room.
            var why = await BusyAsync(DateTime.UtcNow.AddSeconds(length / 10.0 + 300), cancellationToken).ConfigureAwait(false);
            if (why is null)
            {
                return;
            }

            if (DateTime.UtcNow > giveUpUtc)
            {
                throw new InvalidOperationException("gave up waiting for the IPTV connection: " + why);
            }

            if (job.State != CatchupState.Waiting || job.Message != why)
            {
                job.Set(CatchupState.Waiting, why);
                _logger.LogInformation("Comseg: {Title} waits: {Why}", job.Request.Title, why);
            }

            await Task.Delay(BusyPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Why the IPTV connection is not free until <paramref name="untilUtc"/>, or null.</summary>
    private async Task<string?> BusyAsync(DateTime untilUtc, CancellationToken cancellationToken)
    {
        // Every timer: IsActive would leave out the scheduled ones (it means "in progress").
        var timers = await _liveTv.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false);
        foreach (var timer in timers.Items)
        {
            if (timer.Status == RecordingStatus.InProgress)
            {
                return "Jellyfin is recording " + timer.Name;
            }

            var start = timer.StartDate.AddSeconds(-timer.PrePaddingSeconds);
            if (timer.Status == RecordingStatus.New && start < untilUtc && timer.EndDate > DateTime.UtcNow)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Jellyfin has a recording due at {start.ToLocalTime():HH:mm}: {timer.Name}");
            }
        }

        foreach (var session in _sessions.Sessions)
        {
            if (session.NowPlayingItem is { } playing && (playing.Type == BaseItemKind.TvChannel || playing.Type == BaseItemKind.LiveTvChannel))
            {
                return "live TV is playing on " + (session.DeviceName ?? "a device");
            }
        }

        return null;
    }

    private async Task<PlaylistCheck> CheckPlaylistAsync(string url, DateTime askStart, DateTime wantStart, int length, CancellationToken cancellationToken)
    {
        using var response = await _http.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new PlaylistCheck(new[] { string.Create(CultureInfo.InvariantCulture, $"the archive answered HTTP {(int)response.StatusCode}") }, 0, null);
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var final = response.RequestMessage?.RequestUri?.ToString() ?? url;
        return Archive.Check(url, final, text, askStart, wantStart, length, slackSeconds: Math.Max(15, ComsegPlugin.Config.CatchupPadSeconds));
    }

    /// <summary>Copies the archive window into the target. Null when done; otherwise why it stopped to give up the connection.</summary>
    private async Task<string?> DownloadAsync(CatchupJob job, string url, string target, int seconds, CancellationToken cancellationToken)
    {
        var part = target + ".part";
        var info = new ProcessStartInfo
        {
            FileName = _encoder.EncoderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // A segment that stalls past the read timeout, or fails, is tried twice
        // more; after that ffmpeg skips it and still exits 0, so a skip is
        // watched for in its output and fails the download.
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-v", "warning", "-nostats", "-progress", "pipe:1", "-y", "-rw_timeout", ReadTimeoutMicroseconds, "-seg_max_retry", "2", "-i", url, "-map", "0", "-c", "copy", "-f", "mpegts", part })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        var errors = new List<string>();
        var skipped = 0;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line)
            {
                return;
            }

            if (SkippedSegment.IsMatch(line))
            {
                Interlocked.Increment(ref skipped);
            }

            if (errors.Count < 50)
            {
                errors.Add(Url.Replace(line, "<url>"));
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && seconds > 0)
            {
                job.Percent = (int)Math.Clamp(us / 10_000 / seconds, 0, 99);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        var saved = false;
        try
        {
            var nextCheck = DateTime.UtcNow + BusyCheckWhileDownloading;
            while (!process.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (DateTime.UtcNow < nextCheck)
                {
                    continue;
                }

                nextCheck = DateTime.UtcNow + BusyCheckWhileDownloading;
                var stoppedFor = await BusyAsync(DateTime.UtcNow.AddMinutes(5), cancellationToken).ConfigureAwait(false);
                if (stoppedFor is not null)
                {
                    return stoppedFor;
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(part) || new FileInfo(part).Length < 1 << 20)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"ffmpeg failed (exit {process.ExitCode}): {string.Join(" | ", errors.TakeLast(3))}"));
            }

            if (Volatile.Read(ref skipped) is var gaps and > 0)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"the archive did not deliver {gaps} segment(s) of the programme, which would leave a gap"));
            }

            File.Move(part, target);
            saved = true;
            return null;
        }
        finally
        {
            // Every way out but a saved file, failures included, stops ffmpeg
            // before the next job can take the connection, and clears up after it.
            if (!saved)
            {
                await StopAsync(process).ConfigureAwait(false);
                TryDelete(part);
                TryDeleteEmptyFolder(Path.GetDirectoryName(part));
            }
        }
    }

    /// <summary>Kills a process that is still running and waits, briefly, for it to go.</summary>
    private static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // It exited meanwhile, or would not go in time; the file is cleared up either way.
        }
    }

    /// <summary>The poster: the programme's image when it can be fetched from a public address, otherwise a frame from the recording.</summary>
    private async Task<string?> WritePosterAsync(string folder, string video, CatchupRequest request, int seconds, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(request.ImageUrl, UriKind.Absolute, out var image) && (image.Scheme == Uri.UriSchemeHttp || image.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                using var response = await Images.GetAsync(image, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var extension = type switch
                {
                    "image/jpeg" => ".jpg",
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    _ => null,
                };
                if (response.IsSuccessStatusCode && extension is not null && response.Content.Headers.ContentLength is null or <= MaxImageBytes)
                {
                    var bytes = await ReadAtMostAsync(response.Content, MaxImageBytes, cancellationToken).ConfigureAwait(false);
                    if (bytes is { Length: > 0 })
                    {
                        var path = Path.Combine(folder, "poster" + extension);
                        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                        return path;
                    }
                }

                _logger.LogInformation("Comseg: the image for {Title} was not usable ({Status}, {Type}); using a frame", request.Title, (int)response.StatusCode, type);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                _logger.LogInformation("Comseg: the image for {Title} could not be fetched ({Problem}); using a frame", request.Title, ex.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Comseg: the image for {Title} timed out; using a frame", request.Title);
            }
        }

        var poster = Path.Combine(folder, "poster.jpg");
        var at = Math.Min(300, seconds / 10);
        var info = new ProcessStartInfo
        {
            FileName = _encoder.EncoderPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-nostdin", "-v", "error", "-y", "-ss", at.ToString(CultureInfo.InvariantCulture), "-i", video, "-ss", "5", "-frames:v", "1", "-vf", "scale=640:-2", "-q:v", "2", poster })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 && File.Exists(poster) ? poster : null;
    }

    /// <summary>
    /// Asks the library that owns the file to pick it up (a new folder gets no
    /// item by itself; see RecordingWatcher.RequestLibraryRefresh) and waits for its item.
    /// </summary>
    private async Task<MediaBrowser.Controller.Entities.BaseItem?> WaitForItemAsync(string path, CancellationToken cancellationToken)
    {
        var library = _library.GetVirtualFolders()
            .FirstOrDefault(v => v.Locations is not null && v.Locations.Any(l => RecordingPaths.IsUnder(path, new[] { l })));
        if (library is null || !Guid.TryParse(library.ItemId, out var rootId))
        {
            _logger.LogWarning("Comseg: {Path} is in no library", path);
            return null;
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt % 4 == 0)
            {
                _providers.QueueRefresh(
                    rootId,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        RefreshPaths = new[] { path, dir, Path.GetDirectoryName(dir) ?? string.Empty },
                    },
                    RefreshPriority.High);
            }

            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            var item = _library.FindByPath(path, false);
            if (item is not null)
            {
                return item;
            }
        }

        return null;
    }

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

    /// <summary>Connects the image client to a public address of the host, and to nothing else.</summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(PublicAddress.IsPublic)
            ?? throw new HttpRequestException(context.DnsEndPoint.Host + " is not a public address");
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

    /// <summary>Removes a programme folder this job made when nothing was saved in it.</summary>
    private static void TryDeleteEmptyFolder(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
