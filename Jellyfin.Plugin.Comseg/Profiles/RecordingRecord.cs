using System;

namespace Jellyfin.Plugin.Comseg.Profiles;

/// <summary>
/// What the plugin knows about where a recording came from. A finished
/// Jellyfin recording carries no channel, only its timer does while it
/// records, so the watcher writes this down when it first sees the recording
/// and every later pass over the file reads it.
/// </summary>
/// <param name="Path">The recording file.</param>
/// <param name="ChannelName">The channel's name in Jellyfin.</param>
/// <param name="ChannelId">The channel's Jellyfin id, when known.</param>
/// <param name="Programme">The programme's name, when known.</param>
/// <param name="StartUtc">When the programme started, when known.</param>
/// <param name="Source">Who wrote it: "timer", "catchup" or "admin".</param>
public sealed record RecordingRecord(
    string Path,
    string ChannelName,
    string? ChannelId,
    string? Programme,
    DateTime? StartUtc,
    string Source);
