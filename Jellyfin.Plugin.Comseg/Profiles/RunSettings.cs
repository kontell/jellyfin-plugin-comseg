using System.Collections.Generic;
using Jellyfin.Plugin.Comseg.Configuration;

namespace Jellyfin.Plugin.Comseg.Profiles;

/// <summary>The settings one recording is analysed with: the plugin's, changed by the profile its channel matches.</summary>
/// <param name="Config">The plugin configuration with the profile's analysis height applied.</param>
/// <param name="Profile">The profile, or null when none applies.</param>
/// <param name="Record">The recording's record, or null when the plugin does not know its channel.</param>
/// <param name="Keys">The profile's ini keys, laid between the base ini and the run-mode keys.</param>
/// <param name="Problems">Keys of the profile that were left out, and why.</param>
public sealed record RunSettings(
    PluginConfiguration Config,
    ChannelProfile? Profile,
    RecordingRecord? Record,
    IReadOnlyDictionary<string, string> Keys,
    IReadOnlyList<string> Problems)
{
    /// <summary>Gets a value indicating whether the recording is analysed while it records: the plugin's setting, unless the profile turns it off.</summary>
    public bool LiveDetection => Config.EnableLiveDetection && (Profile?.LiveDetection ?? true);

    /// <summary>Gets a value indicating whether the recording's audio is kept and listened to: on when the plugin's setting is, unless the profile turns it off, and only for a recording whose channel is known.</summary>
    public bool AudioEvidence => Config.EnableAudioEvidence && (Profile?.AudioEvidence ?? true) && Record is not null;

    /// <summary>Gets a value indicating whether the audio decision's rows are published while the recording is written: the profile asks for it and audio evidence is on.</summary>
    public bool PublishAudio => AudioEvidence && (Profile?.PublishAudio ?? false);

    /// <summary>Gets a value indicating whether Comskip's per-frame data is kept after a full pass.</summary>
    public bool KeepFrames => Profile?.KeepFrames ?? false;

    /// <summary>Gets the profile's name, or null when none applies.</summary>
    public string? ProfileName => Profile is null ? null : string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Channels : Profile.Name.Trim();

    /// <summary>Gets a short description for the log: the channel and the profile.</summary>
    public string Description => Record is null
        ? "channel unknown, default settings"
        : "channel " + Record.ChannelName + ", " + (ProfileName is { } name ? "profile " + name : "default settings");
}
