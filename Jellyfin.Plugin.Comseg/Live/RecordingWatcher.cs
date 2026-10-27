using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Configuration;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Live;

/// <summary>
/// Finds recordings in progress and runs a live Comskip against each. Polls
/// the timers rather than listening for library events: the recording's item
/// appears a few seconds after the timer starts and nothing announces that.
/// Each poll reads the patched Comskip's periodic analysis, feeds the
/// tracker and publishes what it confirms; when a recording leaves the
/// in-progress set its Comskip is stopped and a full pass replaces the rows.
/// </summary>
public sealed class RecordingWatcher : BackgroundService
{
    private const long MinBytesToStart = 64 * 1024;
    private static readonly TimeSpan MaxJobAge = TimeSpan.FromHours(12);

    private readonly ILiveTvManager _liveTv;
    private readonly IRecordingsManager _recordings;
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ComskipRunner _runner;
    private readonly CompletedPass _pass;
    private readonly SegmentStore _store;
    private readonly Workspace _workspace;
    private readonly DetectionRegistry _registry;
    private readonly ILogger<RecordingWatcher> _logger;
    private readonly ConcurrentDictionary<string, LiveJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _finalizations = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="RecordingWatcher"/> class.</summary>
    /// <param name="liveTv">The Live TV manager.</param>
    /// <param name="recordings">The recordings manager.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="providers">The provider manager, for library refreshes.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="runner">The Comskip runner.</param>
    /// <param name="pass">The full-pass runner.</param>
    /// <param name="store">The segment store.</param>
    /// <param name="workspace">The workspace.</param>
    /// <param name="registry">The detection registry.</param>
    /// <param name="logger">The logger.</param>
    public RecordingWatcher(
        ILiveTvManager liveTv,
        IRecordingsManager recordings,
        ILibraryManager library,
        IProviderManager providers,
        IFileSystem fileSystem,
        ComskipRunner runner,
        CompletedPass pass,
        SegmentStore store,
        Workspace workspace,
        DetectionRegistry registry,
        ILogger<RecordingWatcher> logger)
    {
        _liveTv = liveTv;
        _recordings = recordings;
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
        _runner = runner;
        _pass = pass;
        _store = store;
        _workspace = workspace;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>A snapshot of the live jobs, for the status page.</summary>
    /// <returns>The jobs.</returns>
    public IReadOnlyList<LiveJob> Snapshot() => _jobs.Values.OrderBy(j => j.StartedUtc).ToList();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Comseg: watching for recordings in progress (live detection {State})", ComsegPlugin.Config.EnableLiveDetection ? "on" : "off");
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = ComsegPlugin.Config;
            try
            {
                await PollOnceAsync(config, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Comseg: poll of in-progress recordings failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(config.PollSeconds, 5, 300)), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var job in _jobs.Values)
        {
            job.Process?.Dispose();
        }
    }

    private static bool FileReady(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= MinBytesToStart;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task PollOnceAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var active = await ActivePathsAsync(cancellationToken).ConfigureAwait(false);

        if (config.EnableLiveDetection)
        {
            foreach (var path in active)
            {
                if (!_jobs.TryGetValue(path, out var job))
                {
                    if (_jobs.Count >= Math.Max(1, config.MaxConcurrentLive) || !FileReady(path))
                    {
                        continue;
                    }

                    job = new LiveJob(path, _workspace.JobDir(config, "live", path), new LiveBreakTracker(config.ConfirmPasses, config.MinBreakSeconds));
                    _jobs[path] = job;
                    _logger.LogInformation("Comseg: recording in progress, starting live detection on {Path}", path);
                }

                if (job.Finalizing)
                {
                    continue;
                }

                await ServiceJobAsync(job, config, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var job in _jobs.Values.ToList())
        {
            var ended = !active.Contains(job.Path);
            var tooOld = DateTime.UtcNow - job.StartedUtc > MaxJobAge;
            if ((ended || tooOld) && !job.Finalizing)
            {
                job.Finalizing = true;
                _logger.LogInformation("Comseg: recording {Path} {Why}; finishing", job.Path, ended ? "ended" : "exceeded the job age limit");
                _ = Task.Run(() => FinalizeAsync(job, config, cancellationToken), CancellationToken.None);
            }
        }
    }

    private async Task<HashSet<string>> ActivePathsAsync(CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var timers = await _liveTv.GetTimers(new TimerQuery { IsActive = true }, cancellationToken).ConfigureAwait(false);
        foreach (var timer in timers.Items)
        {
            if (timer.Status != RecordingStatus.InProgress)
            {
                continue;
            }

            // The DTO's Id is a hash Jellyfin makes for clients; the recordings
            // manager keys its active recordings by the service's own timer id,
            // which the DTO carries as ExternalId.
            var timerId = string.IsNullOrEmpty(timer.ExternalId) ? timer.Id : timer.ExternalId;
            if (string.IsNullOrEmpty(timerId))
            {
                continue;
            }

            var path = _recordings.GetActiveRecordingPath(timerId);
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
            else
            {
                _logger.LogDebug("Comseg: timer {Timer} is in progress but has no active recording path yet", timer.Name);
            }
        }

        return paths;
    }

    private async Task ServiceJobAsync(LiveJob job, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (job.Process is null || (job.Process.HasExited && job.Restarts < 2))
        {
            if (job.Process is not null)
            {
                _logger.LogWarning("Comseg: live Comskip exited on its own (code {Code}) while {Path} still records; restarting. Tail: {Tail}", job.Process.ExitCode, job.Path, job.Process.Tail);
                job.Process.Dispose();
                job.Restarts++;
            }

            Directory.CreateDirectory(job.WorkDir);
            var iniPath = Path.Combine(job.WorkDir, "live.ini");
            await File.WriteAllTextAsync(
                iniPath,
                ComskipIni.Compose(ComskipIni.BaseText(config.IniPath), ComskipIni.LiveOverrides(config)),
                cancellationToken).ConfigureAwait(false);
            job.Process = _runner.StartLive(config.ComskipPath, iniPath, job.WorkDir, job.Path);
            if (job.Process is null)
            {
                job.LastError = "Comskip could not be started; check the configured path";
                return;
            }
        }

        if (job.Item is null)
        {
            job.Item = _library.FindByPath(job.Path, false);
            if (job.Item is null)
            {
                RequestLibraryRefresh(job);
            }

            if (job.Item is not null)
            {
                _registry.TryEnter(job.Item.Id);
                await _store.AdoptAsync(job.Item).ConfigureAwait(false);
                if (job.Tracker.Confirmed.Count > 0)
                {
                    job.Published = job.Tracker.Confirmed.Count;
                    await _store.PublishAsync(job.Item, job.Tracker.Confirmed).ConfigureAwait(false);
                }
            }
        }

        var master = job.Process.MasterEdlPath;
        if (!File.Exists(master))
        {
            // The stock Comskip never writes this file: its live list is the
            // plain .edl, and on most feeds it stays empty. Say so once.
            if (job.LastError is null && DateTime.UtcNow - job.StartedUtc > TimeSpan.FromMinutes(4) && !job.Process.HasExited)
            {
                job.LastError = "no periodic analysis after 4 minutes; is the configured Comskip the patched build?";
                _logger.LogWarning("Comseg: {Path}: {Problem}", job.Path, job.LastError);
            }

            return;
        }

        job.LastError = null;

        var written = File.GetLastWriteTimeUtc(master);
        if (written <= job.LastMasterWriteUtc)
        {
            return;
        }

        job.LastMasterWriteUtc = written;
        var breaks = EdlParser.ReadFile(master);
        _logger.LogInformation(
            "Comseg: analysis {Pass} of {Name}: {Breaks}",
            job.Tracker.Passes + 1,
            job.Item?.Name ?? Path.GetFileName(job.Path),
            breaks.Count == 0 ? "no breaks" : string.Join(", ", breaks.Select(b => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0}-{1:0}", b.Start, b.End))));
        if (job.Tracker.Observe(breaks) && job.Item is not null)
        {
            job.Published = job.Tracker.Confirmed.Count;
            await _store.PublishAsync(job.Item, job.Tracker.Confirmed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Jellyfin refreshes a new recording's folder only when a library item for a
    /// parent folder already exists (a series folder from an earlier recording);
    /// a first recording into a new folder waits for the realtime monitor or the
    /// next scan. Ask the owning library root to validate the new paths instead,
    /// the same targeted refresh Jellyfin runs when it can.
    /// </summary>
    private void RequestLibraryRefresh(LiveJob job)
    {
        var now = DateTime.UtcNow;
        if (now - job.LastRefreshRequestUtc < TimeSpan.FromSeconds(60))
        {
            return;
        }

        job.LastRefreshRequestUtc = now;
        var folder = _library.GetVirtualFolders()
            .FirstOrDefault(v => v.Locations is not null && v.Locations.Any(l => RecordingPaths.IsUnder(job.Path, new[] { l })));
        if (folder is null || !Guid.TryParse(folder.ItemId, out var rootId))
        {
            _logger.LogWarning("Comseg: {Path} is in no library; its breaks cannot be stored until it is", job.Path);
            return;
        }

        var dir = Path.GetDirectoryName(job.Path) ?? string.Empty;
        var parent = Path.GetDirectoryName(dir) ?? string.Empty;
        _logger.LogInformation("Comseg: asking library {Library} to pick up {Path}", folder.Name, job.Path);
        _providers.QueueRefresh(
            rootId,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                RefreshPaths = new[] { job.Path, dir, parent },
            },
            RefreshPriority.High);
    }

    private async Task FinalizeAsync(LiveJob job, PluginConfiguration config, CancellationToken cancellationToken)
    {
        try
        {
            job.Process?.Kill();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var item = job.Item ?? _library.FindByPath(job.Path, false);
            if (item is null)
            {
                _logger.LogWarning("Comseg: no library item for {Path}; its live breaks were not stored", job.Path);
                return;
            }

            _registry.TryEnter(item.Id);
            if (!config.EnableCompletedDetection)
            {
                return;
            }

            await _finalizations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var breaks = await _pass.RunAsync(job.Path, config, cancellationToken).ConfigureAwait(false);
                if (breaks is null)
                {
                    _logger.LogWarning("Comseg: final pass over {Path} produced nothing; the live breaks stand", job.Path);
                    return;
                }

                await _store.ReplaceAsync(item, breaks).ConfigureAwait(false);
            }
            finally
            {
                _finalizations.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Comseg: finishing {Path} failed", job.Path);
        }
        finally
        {
            job.Process?.Dispose();
            var itemId = job.Item?.Id;
            if (itemId.HasValue)
            {
                _registry.Exit(itemId.Value);
                _store.Forget(itemId.Value);
            }

            _jobs.TryRemove(job.Path, out _);
            Workspace.TryDelete(job.WorkDir);
        }
    }
}
