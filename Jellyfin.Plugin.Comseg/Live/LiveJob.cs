using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Comseg.Live;

/// <summary>
/// One recording the watcher looks after: while it records, while its final
/// pass runs, and after that until Jellyfin's refresh of the closed file has
/// pruned the rows and the job has put them back. The values other threads
/// set (the provider's prune notice, an item removal, the final pass) are
/// kept so that no read sees half of one.
/// </summary>
public sealed class LiveJob
{
    private readonly object _claimLock = new();
    private Guid? _claimed;
    private volatile BaseItem? _item;
    private volatile IReadOnlyList<Break>? _finalBreaks;
    private long _closedWriteTicks;
    private long _lastPruneTicks;

    /// <summary>Initializes a new instance of the <see cref="LiveJob"/> class.</summary>
    /// <param name="path">The recording file.</param>
    /// <param name="workDir">The job folder.</param>
    /// <param name="tracker">The break tracker.</param>
    public LiveJob(string path, string workDir, LiveBreakTracker tracker)
    {
        Path = path;
        WorkDir = workDir;
        Tracker = tracker;
        StartedUtc = DateTime.UtcNow;
    }

    /// <summary>Gets the recording file.</summary>
    public string Path { get; }

    /// <summary>Gets the job folder.</summary>
    public string WorkDir { get; }

    /// <summary>Gets the break tracker.</summary>
    public LiveBreakTracker Tracker { get; }

    /// <summary>Gets when the job started.</summary>
    public DateTime StartedUtc { get; }

    /// <summary>Gets or sets the library item, once Jellyfin has created it (a few seconds after the timer starts); null again once Jellyfin removes it.</summary>
    public BaseItem? Item
    {
        get => _item;
        set => _item = value;
    }

    /// <summary>Gets or sets the live Comskip process.</summary>
    public ComskipRunner.LiveProcess? Process { get; set; }

    /// <summary>Gets or sets the write time of the last analysis read.</summary>
    public DateTime LastMasterWriteUtc { get; set; }

    /// <summary>Gets or sets how many times the process was restarted after exiting on its own.</summary>
    public int Restarts { get; set; }

    /// <summary>Gets or sets when the library was last asked to refresh the recording's folder.</summary>
    public DateTime LastRefreshRequestUtc { get; set; }

    /// <summary>Gets or sets the final pass, started when the recording left the in-progress set.</summary>
    public Task? Finalization { get; set; }

    /// <summary>Gets a value indicating whether the recording ended and its final pass is running.</summary>
    public bool Finalizing => Finalization is { IsCompleted: false };

    /// <summary>Gets a value indicating whether the final pass is over and the job only keeps the breaks in place until Jellyfin's refresh of the closed file.</summary>
    public bool Finished => Finalization is { IsCompleted: true };

    /// <summary>Gets or sets the final pass's breaks, once it has run; every store from then on uses them.</summary>
    public IReadOnlyList<Break>? FinalBreaks
    {
        get => _finalBreaks;
        set => _finalBreaks = value;
    }

    /// <summary>
    /// Gets the confirmed live breaks once there is anything to store:
    /// something confirmed, or rows stored earlier that a withdrawal has to
    /// remove. Null before that, so a job started over after a restart leaves
    /// the rows the previous run stored until it confirms breaks of its own.
    /// </summary>
    public IReadOnlyList<Break>? LiveBreaks => Tracker.Confirmed.Count > 0 || Published > 0 ? Tracker.Confirmed : null;

    /// <summary>Gets or sets the recording file's last write time when the job saw the recording end.</summary>
    public DateTime ClosedWriteUtc
    {
        get => new(Volatile.Read(ref _closedWriteTicks), DateTimeKind.Utc);
        set => Volatile.Write(ref _closedWriteTicks, value.Ticks);
    }

    /// <summary>Gets or sets when Jellyfin last began pruning the item's segments.</summary>
    public DateTime LastPruneUtc
    {
        get => new(Volatile.Read(ref _lastPruneTicks), DateTimeKind.Utc);
        set => Volatile.Write(ref _lastPruneTicks, value.Ticks);
    }

    /// <summary>Gets or sets until when a finished job waits for Jellyfin's refresh of the closed file.</summary>
    public DateTime GuardUntilUtc { get; set; }

    /// <summary>Gets the lock that orders this job's stores, so a reconcile cannot overwrite the final result.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Gets or sets how many breaks the last store that succeeded left in Jellyfin.</summary>
    public int Published { get; set; }

    /// <summary>Gets or sets the last problem with Comskip or its analysis, for the status page.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets the last problem storing the breaks, for the status page; the next store that succeeds clears it.</summary>
    public string? StoreError { get; set; }

    /// <summary>Gets a value indicating whether the job holds a claim in the detection registry.</summary>
    public bool HoldsClaim
    {
        get
        {
            lock (_claimLock)
            {
                return _claimed is not null;
            }
        }
    }

    /// <summary>Claims the item in the registry unless the job holds a claim already, so the scan and on-demand detection leave it alone while the job works on it.</summary>
    /// <param name="registry">The registry.</param>
    /// <param name="itemId">The item.</param>
    public void Claim(DetectionRegistry registry, Guid itemId)
    {
        lock (_claimLock)
        {
            if (_claimed is null && registry.TryEnter(itemId))
            {
                _claimed = itemId;
            }
        }
    }

    /// <summary>Releases the job's claim, if it holds one.</summary>
    /// <param name="registry">The registry.</param>
    public void Release(DetectionRegistry registry)
    {
        lock (_claimLock)
        {
            if (_claimed is { } id)
            {
                _claimed = null;
                registry.Exit(id);
            }
        }
    }
}
