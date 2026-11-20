using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>
/// The audio decision for one recording in progress, run beside the rows
/// the plugin publishes and publishing nothing itself: what it would have
/// shown goes to the log and the status page, so it can be judged against
/// the published rows and the final pass before it is trusted with clients.
/// </summary>
public sealed class AudioShadow
{
    private IReadOnlyList<Break>? _latest;
    private IReadOnlyList<Break>? _previous;
    private AnalysisStatus? _status;

    /// <summary>Initializes a new instance of the <see cref="AudioShadow"/> class.</summary>
    /// <param name="stored">The channel's stored recordings.</param>
    public AudioShadow(IReadOnlyList<StoredRecording> stored)
    {
        Listener = new AudioListener(stored);
        Trusted = AudioTrust.IsTrusted(stored);
    }

    /// <summary>Gets the listener.</summary>
    public AudioListener Listener { get; }

    /// <summary>Gets a value indicating whether the channel's audio is trusted.</summary>
    public bool Trusted { get; }

    /// <summary>Gets the rows the decision would publish now.</summary>
    public IReadOnlyList<DecidedRow> Rows { get; private set; } = [];

    /// <summary>Gets how many items of the recording have been heard.</summary>
    public int HeardItems => Fingerprint.ItemAt(Listener.Heard);

    /// <summary>Gets a value indicating whether Comskip-only breaks wait for the final pass: the channel's audio is trusted, or the latest analysis was made without the channel logo while the channel has audio history to go by.</summary>
    public bool HoldsBackComskip => Trusted || (_status?.WithoutLogo == true && Listener.StoredCount > 0);

    /// <summary>Notes a new analysis from the live Comskip.</summary>
    /// <param name="breaks">Its breaks.</param>
    /// <param name="status">What the Comseg build said about it, or null from a build that does not say.</param>
    public void Analysed(IReadOnlyList<Break> breaks, AnalysisStatus? status = null)
    {
        _previous = _latest;
        _latest = breaks;
        _status = status;
    }

    /// <summary>Hears the newest audio, searches it and decides again.</summary>
    /// <param name="from">The item the new audio starts at.</param>
    /// <param name="items">The new audio.</param>
    /// <param name="minBreak">The shortest break published.</param>
    /// <returns>True when the rows changed.</returns>
    public bool Update(int from, ReadOnlySpan<uint> items, double minBreak)
    {
        Listener.Hear(from, items);

        // Its own breaks so far, less any still open, say what its repeats are.
        var heard = Listener.Heard;
        Listener.Search(Rows.Select(r => r.Break).Where(b => b.End < heard - 30).ToList());
        var rows = ShadowDecision.Decide(_latest, _previous, Listener.Evidence, minBreak, HoldsBackComskip, Listener.Heard);
        if (rows.SequenceEqual(Rows))
        {
            return false;
        }

        Rows = rows;
        return true;
    }

    /// <summary>The rows as text for the log and the status page: 1246-1480 (audio), ...</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The text.</returns>
    public static string Describe(IEnumerable<DecidedRow> rows)
    {
        var parts = rows.Select(r => string.Create(
            CultureInfo.InvariantCulture,
            $"{r.Break.Start:0}-{r.Break.End:0} ({Source(r.Source)})")).ToList();
        return parts.Count == 0 ? "no breaks" : string.Join(", ", parts);
    }

    private static string Source(RowSource source) => source switch
    {
        RowSource.Audio => "audio",
        RowSource.Comskip => "Comskip",
        RowSource.Audio | RowSource.Comskip => "audio and Comskip",
        _ => "?",
    };
}
