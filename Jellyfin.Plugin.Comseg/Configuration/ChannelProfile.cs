namespace Jellyfin.Plugin.Comseg.Configuration;

/// <summary>
/// Settings for the recordings of some channels: one channel, or several
/// that behave alike (a group). The first profile whose channel list matches
/// a recording's channel applies; a recording that matches none, or whose
/// channel is not known, runs with the plugin's own settings.
/// </summary>
public class ChannelProfile
{
    /// <summary>Gets or sets the profile's name, shown in the status and logs.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channels it applies to, as Jellyfin names them,
    /// separated by semicolons or lines. Case is ignored, and * stands for any
    /// text: "Sky Sports*" matches every channel whose name begins that way.
    /// </summary>
    public string Channels { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets Comskip ini keys laid over the base ini for these channels,
    /// as key=value pairs separated by semicolons or lines. The plugin's
    /// run-mode keys still win over them.
    /// </summary>
    public string IniKeys { get; set; } = string.Empty;

    /// <summary>Gets or sets the analysis height for these channels; -1 uses the plugin's setting, 0 analyses the recording as it is.</summary>
    public int AnalysisHeight { get; set; } = -1;

    /// <summary>Gets or sets a value indicating whether these channels' recordings are analysed while they record; when off, they get the full pass once they finish.</summary>
    public bool LiveDetection { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether these channels' audio is kept and listened to (see the plugin's audio evidence setting).</summary>
    public bool AudioEvidence { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Comskip's per-frame data (its .csv and logo file) is kept after each full pass over these channels' recordings, for tuning.</summary>
    public bool KeepFrames { get; set; }
}
