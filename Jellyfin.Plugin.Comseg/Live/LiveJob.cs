using System;
using Jellyfin.Plugin.Comseg.Comskip;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Comseg.Live;

/// <summary>One recording in progress and the Comskip tailing it.</summary>
public sealed class LiveJob
{
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

    /// <summary>Gets or sets the library item, once Jellyfin has created it (a few seconds after the timer starts).</summary>
    public BaseItem? Item { get; set; }

    /// <summary>Gets or sets the live Comskip process.</summary>
    public ComskipRunner.LiveProcess? Process { get; set; }

    /// <summary>Gets or sets the write time of the last analysis read.</summary>
    public DateTime LastMasterWriteUtc { get; set; }

    /// <summary>Gets or sets how many times the process was restarted after exiting on its own.</summary>
    public int Restarts { get; set; }

    /// <summary>Gets or sets when the library was last asked to refresh the recording's folder.</summary>
    public DateTime LastRefreshRequestUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the recording ended and the final pass is running.</summary>
    public bool Finalizing { get; set; }

    /// <summary>Gets or sets how many breaks are published.</summary>
    public int Published { get; set; }

    /// <summary>Gets or sets the last problem, for the status page.</summary>
    public string? LastError { get; set; }
}
