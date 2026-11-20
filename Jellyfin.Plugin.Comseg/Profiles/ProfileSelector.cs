using Jellyfin.Plugin.Comseg.Configuration;

namespace Jellyfin.Plugin.Comseg.Profiles;

/// <summary>
/// Picks the settings for a recording from its record and the profiles. Only
/// the data path is needed, so the segment provider's graph may hold it (see
/// the note on Jellyfin managers in ComsegSegmentProvider).
/// </summary>
public sealed class ProfileSelector
{
    private readonly RecordingRecords _records;

    /// <summary>Initializes a new instance of the <see cref="ProfileSelector"/> class.</summary>
    /// <param name="records">The recording records.</param>
    public ProfileSelector(RecordingRecords records)
    {
        _records = records;
    }

    /// <summary>The settings a recording runs with.</summary>
    /// <param name="mediaPath">The recording.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The settings.</returns>
    public RunSettings For(string mediaPath, PluginConfiguration config)
        => ProfileRules.Resolve(config, _records.Get(mediaPath));
}
