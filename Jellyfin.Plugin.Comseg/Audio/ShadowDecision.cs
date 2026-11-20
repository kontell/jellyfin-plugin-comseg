using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>Where a row's evidence came from.</summary>
[Flags]
public enum RowSource
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Comskip's analysis.</summary>
    Comskip = 1,

    /// <summary>Audio heard elsewhere.</summary>
    Audio = 2,
}

/// <summary>A row the decision would publish.</summary>
/// <param name="Break">The break.</param>
/// <param name="Source">What it rests on.</param>
public readonly record struct DecidedRow(Break Break, RowSource Source);

/// <summary>
/// Decides the rows of a recording in progress from Comskip's analyses and
/// the audio evidence, by the plan of 2026-11-15:
///
/// A break opens on audio when two advert stretches fall within a minute of
/// each other, or an advert stretch follows a break start the audio implies;
/// one advert match alone never opens a break, because a promo quotes the
/// programme it sells. Its start is the implied edge, else the first advert;
/// its end the implied end edge, else the last advert heard.
///
/// A Comskip break whose end is still growing (the end moved on by more than
/// 20 s since the analysis before) reaches only as far as the previous
/// analysis's end, which this one confirms, or the last advert heard in it,
/// or an implied end edge: never the unread end of the file, where today's
/// rows overrun into the programme for a minute at a time. Edges the audio
/// implies within 20 s replace Comskip's.
///
/// On a channel whose audio is trusted (see <see cref="AudioTrust"/>) the
/// audio leads: a Comskip break with no advert audio in it is held back for
/// the final pass, and Comskip only widens an audio break, by up to 20 s at
/// its start and 60 s at its end, where adverts not heard before sit.
///
/// Programme audio of 8 s or more inside a row cuts it there.
/// </summary>
public static class ShadowDecision
{
    /// <summary>Advert stretches this close belong to one break.</summary>
    public const double Gap = 60;

    /// <summary>How far an implied edge may move a Comskip edge.</summary>
    public const double Snap = 20;

    /// <summary>Decides the rows.</summary>
    /// <param name="latest">Comskip's latest analysis, or null when there is none (audio alone).</param>
    /// <param name="previous">The analysis before it, or null.</param>
    /// <param name="audio">The audio evidence.</param>
    /// <param name="minBreak">The shortest break published, seconds.</param>
    /// <param name="trustAudio">Whether the channel's audio is trusted.</param>
    /// <returns>The rows, in order.</returns>
    public static IReadOnlyList<DecidedRow> Decide(IReadOnlyList<Break>? latest, IReadOnlyList<Break>? previous, AudioEvidence audio, double minBreak, bool trustAudio = false)
    {
        var adverts = audio.Adverts;
        var edges = audio.Edges;
        var rows = new List<DecidedRow>();

        // Whether each audio break's start and end are edges the audio implies, which Comskip may not move.
        var known = new List<(bool Start, bool End)>();

        // Breaks the audio alone shows.
        foreach (var group in Groups(adverts))
        {
            var first = group[0];
            var last = group[^1];
            var start = Nearest(edges, true, first.Start, first.Start - Gap, first.Start + 5);
            if (group.Count < 2 && start is null)
            {
                continue;
            }

            var end = Nearest(edges, false, last.End, last.End - 5, last.End + 30);
            rows.Add(new DecidedRow(new Break(start ?? first.Start, end ?? last.End), RowSource.Audio));
            known.Add((start is not null, end is not null));
        }

        // Comskip's breaks, held to what is confirmed.
        var audioRows = rows.ToList();
        var comskipRows = new List<DecidedRow>();
        foreach (var b in latest ?? [])
        {
            var before = previous?.FirstOrDefault(p => Math.Abs(p.Start - b.Start) <= 5);
            var growing = before is { } p0 && b.End - p0.End > 20;
            var start = Nearest(edges, true, b.Start, b.Start - Snap, b.Start + Snap) ?? b.Start;
            double end;
            if (growing)
            {
                var heard = adverts.Where(a => a.End > b.Start && a.Start < b.End).Select(a => a.End).DefaultIfEmpty(double.MinValue).Max();
                var edge = Nearest(edges, false, b.End, b.Start, b.End);
                end = Math.Max(before!.Value.End, Math.Max(heard, edge ?? double.MinValue));
                end = Math.Min(end, b.End);
            }
            else
            {
                end = Nearest(edges, false, b.End, b.End - Snap, b.End + Snap) ?? b.End;
            }

            if (end > start)
            {
                comskipRows.Add(new DecidedRow(new Break(start, end), RowSource.Comskip));
            }
        }

        if (trustAudio)
        {
            rows = audioRows.Select((r, i) => Widen(r, known[i], comskipRows)).ToList();
        }
        else
        {
            rows.AddRange(comskipRows);
        }

        return Cut(Merge(rows), audio.Programme).Where(r => r.Break.Length >= minBreak).ToList();
    }

    /// <summary>An audio break widened by the Comskip breaks that overlap it, a little at each end the audio did not fix.</summary>
    private static DecidedRow Widen(DecidedRow row, (bool Start, bool End) known, List<DecidedRow> comskip)
    {
        var b = row.Break;
        var source = row.Source;
        foreach (var c in comskip.Where(c => c.Break.Start < b.End && b.Start < c.Break.End))
        {
            var start = !known.Start && c.Break.Start >= b.Start - Snap ? Math.Min(b.Start, c.Break.Start) : b.Start;
            var end = !known.End && c.Break.End <= b.End + 60 ? Math.Max(b.End, c.Break.End) : b.End;
            b = new Break(start, end);
            source |= RowSource.Comskip;
        }

        return new DecidedRow(b, source);
    }

    /// <summary>Advert stretches grouped by the gap between them.</summary>
    private static List<List<AudioSpan>> Groups(IReadOnlyList<AudioSpan> adverts)
    {
        var groups = new List<List<AudioSpan>>();
        foreach (var span in adverts)
        {
            if (groups.Count > 0 && span.Start - groups[^1][^1].End <= Gap)
            {
                groups[^1].Add(span);
            }
            else
            {
                groups.Add([span]);
            }
        }

        return groups;
    }

    /// <summary>The implied edge of a kind nearest a time, within a range.</summary>
    private static double? Nearest(IReadOnlyList<AudioEdge> edges, bool start, double near, double from, double to)
    {
        double? best = null;
        foreach (var e in edges)
        {
            if (e.IsStart == start && e.At >= from && e.At <= to && (best is null || Math.Abs(e.At - near) < Math.Abs(best.Value - near)))
            {
                best = e.At;
            }
        }

        return best;
    }

    private static List<DecidedRow> Merge(List<DecidedRow> rows)
    {
        var merged = new List<DecidedRow>();
        foreach (var row in rows.OrderBy(r => r.Break.Start))
        {
            if (merged.Count > 0 && row.Break.Start <= merged[^1].Break.End)
            {
                var last = merged[^1];
                merged[^1] = new DecidedRow(
                    new Break(last.Break.Start, Math.Max(last.Break.End, row.Break.End)),
                    last.Source | row.Source);
            }
            else
            {
                merged.Add(row);
            }
        }

        return merged;
    }

    private static IEnumerable<DecidedRow> Cut(List<DecidedRow> rows, IReadOnlyList<AudioSpan> programme)
    {
        foreach (var row in rows)
        {
            var pieces = new List<Break> { row.Break };
            foreach (var p in programme.Where(p => p.End - p.Start >= 8))
            {
                pieces = pieces.SelectMany(b => p.End <= b.Start || p.Start >= b.End
                    ? new[] { b }
                    : new[] { new Break(b.Start, p.Start), new Break(p.End, b.End) }.Where(x => x.Length > 0)).ToList();
            }

            foreach (var piece in pieces)
            {
                yield return new DecidedRow(piece, row.Source);
            }
        }
    }
}
