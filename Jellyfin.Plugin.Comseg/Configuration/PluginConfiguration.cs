using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Comseg.Configuration;

/// <summary>
/// Plugin settings. Paths are the server's; the work directory defaults to
/// a folder under Jellyfin's data path and the ini to the embedded default.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the Comskip executable (a bare name is looked up on PATH).</summary>
    public string ComskipPath { get; set; } = "comskip";

    /// <summary>Gets or sets a comskip.ini to use instead of the embedded default. Empty means the default.</summary>
    public string IniPath { get; set; } = string.Empty;

    /// <summary>Gets or sets where Comskip writes its output. Empty means &lt;data&gt;/comseg.</summary>
    public string WorkDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether recordings are analysed while they are being written.</summary>
    public bool EnableLiveDetection { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether finished recordings get a full pass (the scheduled Media Segment Scan, and the end of a live job).</summary>
    public bool EnableCompletedDetection { get; set; } = true;

    /// <summary>Gets or sets how often (seconds) in-progress recordings are polled for.</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Gets or sets how many seconds of content pass between the patched Comskip's break-finding passes over a recording in progress (live_master_interval); each pass reuses the frames already decoded.</summary>
    public int LiveMasterIntervalSeconds { get; set; } = 60;

    /// <summary>Gets or sets Comskip's live_tv_retries: how many 4 s waits for new data before it gives up on a stalled recording.</summary>
    public int LiveTvRetries { get; set; } = 300;

    /// <summary>Gets or sets how many consecutive analyses must report a break before it is published.</summary>
    public int ConfirmPasses { get; set; } = 2;

    /// <summary>Gets or sets the shortest break (seconds) that is published.</summary>
    public int MinBreakSeconds { get; set; } = 20;

    /// <summary>Gets or sets how many recordings may be analysed live at once.</summary>
    public int MaxConcurrentLive { get; set; } = 2;

    /// <summary>Gets or sets the picture height Comskip analyses at: 0 analyses recordings as they are; 720 or 480 analyses a copy scaled down on the GPU (VA-API) whenever the recording is taller.</summary>
    public int AnalysisHeight { get; set; }

    /// <summary>Gets or sets the VA-API render device that makes the scaled copy.</summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";

    /// <summary>Gets or sets Comskip's decoder thread count.</summary>
    public int ThreadCount { get; set; } = 4;

    /// <summary>Gets or sets the time limit (minutes) for a full pass over a finished recording.</summary>
    public int CompletedPassTimeoutMinutes { get; set; } = 90;

    /// <summary>Gets or sets extra folders whose videos count as recordings, separated by semicolons or lines, besides Jellyfin's own recording folders.</summary>
    public string ExtraRecordingPaths { get; set; } = string.Empty;
}
