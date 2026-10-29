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
using Jellyfin.Plugin.Comseg.Providers;
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
/// tracker and stores what it confirms; when a recording leaves the
/// in-progress set its Comskip is stopped and a full pass replaces the rows.
///
/// Jellyfin deletes an item's segments whenever a refresh notices its file
/// changed: during a recording, and at least once when the file closes,
/// usually while the final pass is still running. The provider hears of
/// each prune just before the delete, and the job that owns the item puts
/// its breaks back straight after; every poll also reconciles the rows, in
/// case they went some other way. Once the final pass is over the job stays,
/// doing nothing else, until Jellyfin's refresh of the closed file has
/// pruned, for up to <see cref="GuardLimit"/>: that refresh can come after
/// the pass, and a prune with no job left to repair it would leave a viewer
/// catching up without breaks until the next scan.
/// </summary>
public sealed class RecordingWatcher : BackgroundService
{
    private const long MinBytesToStart = 64 * 1024;
    private static readonly TimeSpan MaxJobAge = TimeSpan.FromHours(12);

    /// <summary>
    /// How long a finished job waits for Jellyfin's refresh of the closed file.
    /// The refresh normally comes within seconds, but it queues behind any
    /// library refresh already running, and it may not prune at all when an
    /// earlier refresh already saw the final write.
    /// </summary>
    private static readonly TimeSpan GuardLimit = TimeSpan.FromHours(6);

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

    /// <summary>Recordings in progress left alone because their library disables the provider; the poll loop's own.</summary>
    private readonly HashSet<string> _optedOut = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Called by the provider just before Jellyfin deletes an item's segments,
    /// which it does when a refresh noticed the file changed. The job that owns
    /// the item notes the prune and puts its breaks back once the delete has
    /// landed.
    /// </summary>
    /// <param name="itemId">The item being pruned.</param>
    public void OnPruning(Guid itemId)
    {
        var now = DateTime.UtcNow;
        string? prunedPath = null;
        var looked = false;
        foreach (var job in _jobs.Values)
        {
            bool owns;
            if (job.Item is { } item)
            {
                owns = item.Id == itemId;
            }
            else
            {
                // A job that has not found its item has stored nothing, but once
                // finished it still needs to know the closed file's refresh came.
                if (!looked)
                {
                    prunedPath = _library.GetItemById(itemId)?.Path;
                    looked = true;
                }

                owns = prunedPath is not null && string.Equals(job.Path, prunedPath, StringComparison.Ordinal);
            }

            if (owns)
            {
                job.LastPruneUtc = now;
                _ = Task.Run(() => RepairAsync(job));
            }
        }
    }

    /// <summary>
    /// Retires a finished job for the item: a newer pass (on demand, or the
    /// scan's) has the item now, so the job must not put its own breaks back.
    /// </summary>
    /// <param name="itemId">The item.</param>
    public void StandDown(Guid itemId)
    {
        foreach (var job in _jobs.Values)
        {
            if (job.Finished && job.Item?.Id == itemId)
            {
                job.Item = null;
                Retire(job);
            }
        }
    }

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

        _library.ItemRemoved += OnItemRemoved;
        try
        {
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
        }
        finally
        {
            _library.ItemRemoved -= OnItemRemoved;
            foreach (var job in _jobs.Values)
            {
                job.Process?.Dispose();
            }
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
        _optedOut.IntersectWith(active);

        if (config.EnableLiveDetection)
        {
            foreach (var path in active)
            {
                if (!_jobs.TryGetValue(path, out var job))
                {
                    // A finished job only keeps its rows in place; it runs no Comskip.
                    if (_jobs.Values.Count(j => !j.Finished) >= Math.Max(1, config.MaxConcurrentLive) || !FileReady(path) || IsOptedOut(path))
                    {
                        continue;
                    }

                    job = new LiveJob(path, _workspace.JobDir(config, "live", path), new LiveBreakTracker(config.ConfirmPasses, config.MinBreakSeconds));
                    _jobs[path] = job;
                    _logger.LogInformation("Comseg: recording in progress, starting live detection on {Path}", path);
                }

                if (job.Finalization is not null)
                {
                    continue;
                }

                try
                {
                    await ServiceJobAsync(job, config, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One recording's trouble must not keep the others from being
                    // served, finished or retired.
                    job.LastError = ex.Message;
                    _logger.LogWarning(ex, "Comseg: serving the live job for {Path} failed", job.Path);
                }
            }
        }

        foreach (var job in _jobs.Values.ToList())
        {
            try
            {
                await AdvanceAsync(job, active, config, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Comseg: moving the job for {Path} on failed", job.Path);
            }
        }
    }

    /// <summary>
    /// Moves a job on once its recording has left the in-progress set: starts
    /// the final pass, keeps the rows in place while it runs, and retires the
    /// job once there is nothing left to keep in place.
    /// </summary>
    private async Task AdvanceAsync(LiveJob job, HashSet<string> active, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (job.Finalization is null)
        {
            var ended = !active.Contains(job.Path);
            var tooOld = DateTime.UtcNow - job.StartedUtc > MaxJobAge;
            if (!ended && !tooOld)
            {
                return;
            }

            _logger.LogInformation("Comseg: recording {Path} {Why}; finishing", job.Path, ended ? "ended" : "exceeded the job age limit");
            job.ClosedWriteUtc = File.GetLastWriteTimeUtc(job.Path);
            job.Finalization = Task.Run(() => FinalizeAsync(job, config, cancellationToken), CancellationToken.None);
        }

        if (!job.Finished)
        {
            // The final pass takes a while; meanwhile Jellyfin's own refresh at
            // completion prunes the rows. Keep the live breaks in place.
            job.Item ??= _library.FindByPath(job.Path, false);
            await ReconcileAsync(job, "final pass running").ConfigureAwait(false);
            return;
        }

        if (job.GuardUntilUtc == default)
        {
            job.GuardUntilUtc = DateTime.UtcNow + GuardLimit;
        }

        if (!KeepsBreaksInPlace(job))
        {
            Retire(job);
        }
    }

    /// <summary>
    /// Whether a finished job still keeps its breaks in place: until Jellyfin's
    /// refresh of the closed file has pruned the rows (the repair that prune
    /// started puts them back), for <see cref="GuardLimit"/> at most, and only
    /// while it has rows to keep and no other pass owns the item.
    /// </summary>
    private bool KeepsBreaksInPlace(LiveJob job)
    {
        if (job.Item is not { } item || (job.FinalBreaks ?? job.LiveBreaks) is not { Count: > 0 })
        {
            return false;
        }

        return job.LastPruneUtc <= job.ClosedWriteUtc
            && DateTime.UtcNow < job.GuardUntilUtc
            && !_registry.Contains(item.Id);
    }

    private void Retire(LiveJob job)
    {
        job.Release(_registry);
        _jobs.TryRemove(new KeyValuePair<string, LiveJob>(job.Path, job));
        _logger.LogDebug("Comseg: done with {Path}", job.Path);
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

    /// <summary>
    /// Jellyfin's per-library opt-out: a recording whose every library lists the
    /// provider as disabled is not analysed at all, since clients would not be
    /// shown its rows. A recording in no library is still analysed, in case one
    /// is added.
    /// </summary>
    private bool IsOptedOut(string path)
    {
        var holders = _library.GetVirtualFolders()
            .Where(v => v.Locations is not null && RecordingPaths.IsUnder(path, v.Locations))
            .ToList();
        var optedOut = holders.Count > 0 && holders.All(v =>
            v.LibraryOptions?.DisabledMediaSegmentProviders?.Contains(ComsegSegmentProvider.ProviderName, StringComparer.OrdinalIgnoreCase) == true);
        if (optedOut && _optedOut.Add(path))
        {
            _logger.LogInformation("Comseg: {Path} is in a library with the Comseg provider disabled; leaving it alone", path);
        }

        return optedOut;
    }

    private async Task ServiceJobAsync(LiveJob job, PluginConfiguration config, CancellationToken cancellationToken)
    {
        try
        {
            await StartComskipAsync(job, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unwritable work folder or an unreadable ini: no analysis, but the
            // breaks found so far still have to be kept in place.
            job.LastError = "Comskip could not be started: " + ex.Message;
            _logger.LogWarning(ex, "Comseg: starting the live Comskip for {Path} failed", job.Path);
        }

        if (job.Item is null)
        {
            job.Item = _library.FindByPath(job.Path, false);
            if (job.Item is null)
            {
                RequestLibraryRefresh(job);
            }
        }

        if (job.Item is { } item)
        {
            job.Claim(_registry, item.Id);
        }

        if (job.Process is { } process)
        {
            ReadAnalysis(job, process);
        }

        // Reconcile every poll, not only when an analysis changed something: rows
        // can go in ways the provider does not hear of.
        await ReconcileAsync(job, null).ConfigureAwait(false);
    }

    /// <summary>Starts the live Comskip, or restarts it (twice at most) when it exited while the recording still runs.</summary>
    private async Task StartComskipAsync(LiveJob job, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (job.Process is { } current && (!current.HasExited || job.Restarts >= 2))
        {
            return;
        }

        if (job.Process is { } exited)
        {
            _logger.LogWarning("Comseg: live Comskip exited on its own (code {Code}) while {Path} still records; restarting. Tail: {Tail}", exited.ExitCode, job.Path, exited.Tail);
            exited.Dispose();
            job.Process = null;
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
        }
    }

    /// <summary>Reads the patched Comskip's periodic analysis when it has written a new one, and feeds it to the tracker.</summary>
    private void ReadAnalysis(LiveJob job, ComskipRunner.LiveProcess process)
    {
        var master = process.MasterEdlPath;
        if (!File.Exists(master))
        {
            // The stock Comskip never writes this file: its live list is the
            // plain .edl, and on most feeds it stays empty. Say so once.
            if (job.LastError is null && DateTime.UtcNow - job.StartedUtc > TimeSpan.FromMinutes(4) && !process.HasExited)
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
        job.Tracker.Observe(breaks);
    }

    /// <summary>
    /// Makes the item's rows match the job's breaks: the final result once the
    /// final pass has run, else the confirmed live breaks once there is
    /// anything to store. The breaks are chosen under the job's lock, so a
    /// reconcile that queued behind the final store stores the final result too.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="occasion">What prompted the store, for the log, or null.</param>
    /// <returns>How many rows changed, or null when nothing was stored.</returns>
    private async Task<int?> ReconcileAsync(LiveJob job, string? occasion)
    {
        await job.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var item = job.Item;
            var final = job.FinalBreaks;
            var breaks = final ?? job.LiveBreaks;
            if (item is null || breaks is null)
            {
                return null;
            }

            // Another pass (on demand, or the scan's) owns the item; its result wins.
            if (!job.HoldsClaim && _registry.Contains(item.Id))
            {
                return null;
            }

            var why = (final is null ? "live" : "final") + (occasion is null ? string.Empty : ", " + occasion);
            var changes = await _store.PublishAsync(item, breaks, why, final is not null).ConfigureAwait(false);
            job.Published = breaks.Count;
            job.StoreError = null;
            return changes;
        }
        catch (Exception ex)
        {
            job.StoreError = "storing the breaks failed: " + ex.Message;
            _logger.LogWarning(ex, "Comseg: storing the breaks for {Path} failed", job.Path);
            return null;
        }
        finally
        {
            job.Gate.Release();
        }
    }

    /// <summary>
    /// Puts a job's breaks back after Jellyfin pruned them. The provider hears
    /// of a prune just before the delete, so wait for the delete to land: a
    /// reconcile that finds every row still in place ran too early and tries
    /// again, for half a minute at most.
    /// </summary>
    private async Task RepairAsync(LiveJob job)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        for (var attempt = 0; attempt < 6; attempt++, delay *= 2)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (await ReconcileAsync(job, "put back after a prune").ConfigureAwait(false) != 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// A job whose item Jellyfin removed (a viewer deleted the recording, or a
    /// series timer's keep-up-to limit did) stops storing at once, and drops
    /// what a store already under way writes under the dead id: segment rows
    /// are not tied to their item, so nothing else would remove them.
    /// </summary>
    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        foreach (var job in _jobs.Values)
        {
            if (job.Item is { } item && item.Id == e.Item.Id)
            {
                job.Item = null;
                job.Release(_registry);
                _ = Task.Run(() => ForgetAsync(job, item));
            }
        }
    }

    private async Task ForgetAsync(LiveJob job, BaseItem item)
    {
        await job.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _store.ForgetAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Comseg: dropping the rows of removed {Name} failed", item.Name);
        }
        finally
        {
            job.Gate.Release();
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
            job.Item ??= _library.FindByPath(job.Path, false);
            if (job.Item is not { } item)
            {
                _logger.LogWarning("Comseg: no library item for {Path}; its live breaks were not stored", job.Path);
                return;
            }

            job.Claim(_registry, item.Id);
            if (!job.HoldsClaim)
            {
                // An on-demand pass or the scan's got there first; its result stands.
                _logger.LogInformation("Comseg: another pass is running on {Name}; the final pass is left to it", item.Name);
                job.Item = null;
                return;
            }

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

                // From here on every store, this one and any repair after a
                // prune, uses the final result.
                job.FinalBreaks = breaks;
                if (await ReconcileAsync(job, null).ConfigureAwait(false) == 0)
                {
                    _logger.LogInformation("Comseg: the final pass over {Name} agrees with the stored breaks", item.Name);
                }
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
            Workspace.TryDelete(job.WorkDir);

            // The pass is over: on-demand detection and the scan may have the item.
            job.Release(_registry);
        }
    }
}
