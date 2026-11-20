using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>
/// How far a channel's breaks can be told by their audio: for each stored
/// recording, the share of its breaks in which advert audio from the
/// recordings stored before it was heard. On a channel where that is nearly
/// always so (ITV's sponsor clips open and close every break), a break
/// Comskip reports with no audio evidence at all is more likely Comskip's
/// mistake than an unknown break, and the decision holds it back for the
/// final pass.
/// </summary>
public static class AudioTrust
{
    /// <summary>A break needs this much advert audio heard in it to count as covered.</summary>
    public const double MinHeard = 10;

    /// <summary>The share of covered breaks at which a channel's audio is trusted.</summary>
    public const double Trusted = 0.85;

    /// <summary>The share of a recording's breaks (20 s or more) with advert audio from other recordings.</summary>
    /// <param name="items">Its fingerprint.</param>
    /// <param name="breaks">Its breaks.</param>
    /// <param name="others">The channel's other stored recordings.</param>
    /// <returns>The share, or null when it has no breaks or there are no others.</returns>
    public static double? Coverage(uint[] items, IReadOnlyList<Break> breaks, IReadOnlyList<StoredRecording> others)
    {
        var counted = breaks.Where(b => b.Length >= 20).ToList();
        if (counted.Count == 0 || others.Count == 0)
        {
            return null;
        }

        var evidence = new AudioEvidence();
        foreach (var other in others)
        {
            foreach (var run in Matcher.Match(other.Items, other.Index, items))
            {
                var (span, _) = AudioLabels.Label(Fingerprint.TimeOf(run.BStart), Fingerprint.TimeOf(run.AStart), run.Length * Fingerprint.Hop, other.Info.Breaks);
                if (span is { Kind: AudioKind.Advert } s)
                {
                    evidence.Add([s], []);
                }
            }
        }

        var adverts = evidence.Adverts;
        var covered = counted.Count(b => adverts.Sum(a => Math.Max(0, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start))) >= MinHeard);
        return covered / (double)counted.Count;
    }

    /// <summary>Whether a channel's audio is trusted: two stored recordings or more with a coverage, averaging at least <see cref="Trusted"/>.</summary>
    /// <param name="stored">The channel's stored recordings.</param>
    /// <returns>True when trusted.</returns>
    public static bool IsTrusted(IReadOnlyList<StoredRecording> stored)
    {
        var known = stored.Select(s => s.Info.Coverage).OfType<double>().ToList();
        return known.Count >= 2 && known.Average() >= Trusted;
    }
}
