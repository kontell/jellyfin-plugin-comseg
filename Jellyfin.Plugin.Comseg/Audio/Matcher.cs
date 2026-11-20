using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>A stretch of audio two fingerprints share.</summary>
/// <param name="AStart">Where it starts in the indexed fingerprint, in items.</param>
/// <param name="BStart">Where it starts in the searched one, in items.</param>
/// <param name="Length">How long it is, in items.</param>
/// <param name="BitError">The mean share of differing bits over it.</param>
public readonly record struct MatchRun(int AStart, int BStart, int Length, double BitError)
{
    /// <summary>Gets the offset from the searched fingerprint to the indexed one, in items.</summary>
    public int Diagonal => AStart - BStart;
}

/// <summary>
/// Finds the audio two Chromaprint fingerprints share, as the 2026-11-15 study
/// did: items with the same value seed candidate alignments, an alignment
/// with enough seeds on it is walked with a 24-item (3 s) moving bit error,
/// and a run where that stays under 0.2 for at least 32 items (4 s) is a
/// match. Same audio sits at about 0.1, unrelated audio near 0.5.
/// </summary>
public static class Matcher
{
    /// <summary>Seeds on an alignment and its neighbours before it is walked.</summary>
    public const int MinSeeds = 6;

    /// <summary>The moving window, in items.</summary>
    public const int Window = 24;

    /// <summary>The moving bit error that still counts as the same audio.</summary>
    public const double MaxBitError = 0.20;

    /// <summary>A single item worse than this is trimmed off a run's ends.</summary>
    public const double BadItem = 0.25;

    /// <summary>The shortest run kept, in items (about 4 s).</summary>
    public const int MinItems = 32;

    /// <summary>
    /// The runs of audio a searched fingerprint shares with an indexed one.
    /// </summary>
    /// <param name="a">The indexed fingerprint.</param>
    /// <param name="index">Its index.</param>
    /// <param name="b">The searched fingerprint, or a stretch of one.</param>
    /// <param name="minDiagonal">Only alignments with a.position - b.position at least this; for searching a recording against its own earlier part.</param>
    /// <returns>The runs, by where they start in <paramref name="b"/>.</returns>
    public static IReadOnlyList<MatchRun> Match(ReadOnlySpan<uint> a, FingerprintIndex index, ReadOnlySpan<uint> b, int minDiagonal = int.MinValue)
    {
        var seeds = new Dictionary<int, int>();
        for (var j = 0; j < b.Length; j++)
        {
            foreach (var i in index.Find(b[j]))
            {
                var d = i - j;
                if (d >= minDiagonal)
                {
                    seeds[d] = seeds.GetValueOrDefault(d) + 1;
                }
            }
        }

        var runs = new List<MatchRun>();
        foreach (var d in seeds.Keys)
        {
            if (seeds.GetValueOrDefault(d - 1) + seeds[d] + seeds.GetValueOrDefault(d + 1) >= MinSeeds)
            {
                Walk(a, b, d, runs);
            }
        }

        // Neighbouring alignments find the same run; keep the longest of each.
        var kept = new List<MatchRun>();
        foreach (var run in runs.OrderByDescending(r => r.Length).ThenBy(r => r.BStart))
        {
            var dup = false;
            for (var k = 0; k < kept.Count; k++)
            {
                var other = kept[k];
                if (Math.Abs(run.Diagonal - other.Diagonal) <= 3 && run.BStart < other.BStart + other.Length && other.BStart < run.BStart + run.Length)
                {
                    dup = true;
                    break;
                }
            }

            if (!dup)
            {
                kept.Add(run);
            }
        }

        return kept.OrderBy(r => r.BStart).ToList();
    }

    /// <summary>The runs along one alignment, a[j + d] against b[j].</summary>
    private static void Walk(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, int d, List<MatchRun> runs)
    {
        var j0 = Math.Max(0, -d);
        var j1 = Math.Min(b.Length, a.Length - d);
        var n = j1 - j0;
        if (n < Window)
        {
            return;
        }

        var x = new double[n];
        var sums = new double[n + 1];
        for (var k = 0; k < n; k++)
        {
            x[k] = BitOperations.PopCount(a[j0 + k + d] ^ b[j0 + k]) / 32.0;
            sums[k + 1] = sums[k] + x[k];
        }

        // good[k]: the window x[k .. k + Window) is under the limit.
        var windows = n - Window + 1;
        var k0 = 0;
        while (k0 < windows)
        {
            if ((sums[k0 + Window] - sums[k0]) / Window >= MaxBitError)
            {
                k0++;
                continue;
            }

            var k1 = k0;
            while (k1 < windows && (sums[k1 + Window] - sums[k1]) / Window < MaxBitError)
            {
                k1++;
            }

            // The items the good windows cover, less smeared ends.
            var lo = k0;
            var hi = k1 - 1 + Window;
            while (lo < hi && x[lo] > BadItem)
            {
                lo++;
            }

            while (hi > lo && x[hi - 1] > BadItem)
            {
                hi--;
            }

            if (hi - lo >= MinItems)
            {
                runs.Add(new MatchRun(j0 + lo + d, j0 + lo, hi - lo, (sums[hi] - sums[lo]) / (hi - lo)));
            }

            k0 = k1;
        }
    }
}
