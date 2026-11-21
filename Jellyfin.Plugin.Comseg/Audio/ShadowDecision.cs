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
/// each other, or an advert stretch is an edge clip, one that marked a break's
/// start or end in a stored recording (its role here comes from context);
/// one advert match alone never opens a break, because a promo quotes the
/// programme it sells. Its start is the implied edge, else the first advert;
/// its end the implied end edge, else the last advert heard.
///
/// Advert stretches within a minute of each other belong to one break, and so
/// does what lies between them. A break that opened on an edge clip and has
/// not yet closed on one also takes in a stretch up to three minutes after
/// the last, as long as no programme audio lies between and the break stays
/// under six minutes: adverts first aired since the stored recordings fill a
/// break's middle and end.
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

    /// <summary>The longest gap between advert stretches that a break opened by an edge clip still bridges.</summary>
    public const double Bridge = 180;

    /// <summary>The longest break a bridge may make.</summary>
    public const double MaxBreak = 360;

    /// <summary>How far into a break an end edge must be to close it rather than echo its opening clip.</summary>
    public const double MinClosed = 30;

    /// <summary>Programme audio at least this long is trusted: it cuts a row, and no bridge crosses it.</summary>
    public const double ProgrammeCut = 8;

    /// <summary>Decides the rows.</summary>
    /// <param name="latest">Comskip's latest analysis, or null when there is none (audio alone).</param>
    /// <param name="previous">The analysis before it, or null.</param>
    /// <param name="audio">The audio evidence.</param>
    /// <param name="minBreak">The shortest break published, seconds.</param>
    /// <param name="trustAudio">Whether the channel's audio is trusted.</param>
    /// <param name="heard">How far the recording has been heard: a break the audio found that ends within 30 s of it may still be on air, and is not held to the shortest length yet.</param>
    /// <returns>The rows, in order.</returns>
    public static IReadOnlyList<DecidedRow> Decide(IReadOnlyList<Break>? latest, IReadOnlyList<Break>? previous, AudioEvidence audio, double minBreak, bool trustAudio = false, double heard = double.MaxValue)
    {
        var adverts = audio.Adverts;
        var edges = audio.Edges;
        var rows = new List<DecidedRow>();

        // Whether each audio break's start and end are edges the audio implies, which Comskip may not move.
        var known = new List<(bool Start, bool End)>();

        // Breaks the audio alone shows.
        foreach (var group in Groups(adverts, edges, audio.Programme))
        {
            var first = group[0];
            var last = group[^1];
            var start = Nearest(edges, true, first.Start, first.Start - Gap, first.Start + 5);
            var edgeClip = OpensOnEdgeClip(first, edges);
            if (audio.AdvertMatchesIn(first.Start, last.End) < 2 && !edgeClip)
            {
                continue;
            }

            // A clip that closed the stored breaks and opens this one: the ends it implies are its old role's.
            var openedByClosingClip = start is null && edgeClip;
            var end = Nearest(edges.Where(e => !openedByClosingClip || e.At > first.End + AudioLabels.EdgeReach + 1).ToList(), false, last.End, last.End - 5, last.End + 30);
            rows.Add(new DecidedRow(new Break(start ?? first.Start, end ?? last.End), RowSource.Audio));
            known.Add((start is not null, end is not null));
        }

        // Comskip's breaks, held to what is confirmed.
        var audioRows = rows.ToList();
        var comskipRows = new List<DecidedRow>();
        foreach (var b in latest ?? [])
        {
            Break? before = previous?.Where(p => Math.Abs(p.Start - b.Start) <= 5).Select(p => (Break?)p).FirstOrDefault();
            var growing = before is { } p0 && b.End - p0.End > 20;
            var start = Nearest(edges, true, b.Start, b.Start - Snap, b.Start + Snap) ?? b.Start;
            double end;
            if (growing)
            {
                var heardAdverts = adverts.Where(a => a.End > b.Start && a.Start < b.End).Select(a => a.End).DefaultIfEmpty(double.MinValue).Max();
                var edge = Nearest(edges, false, b.End, b.Start, b.End);
                end = Math.Max(before!.Value.End, Math.Max(heardAdverts, edge ?? double.MinValue));
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

        return Cut(Merge(rows), audio.Programme)
            .Where(r => r.Break.Length >= minBreak || ((r.Source & RowSource.Audio) != 0 && r.Break.End > heard - 30))
            .ToList();
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

    /// <summary>Whether a break's first advert stretch is an edge clip, whichever edge it marked before: sponsors swap ends between broadcasts, so a clip that closed the stored breaks can open this one.</summary>
    private static bool OpensOnEdgeClip(AudioSpan first, IReadOnlyList<AudioEdge> edges) =>
        Nearest(edges, true, first.Start, first.Start - Gap, first.Start + 5) is not null
        || edges.Any(e => !e.IsStart && e.At >= first.Start && e.At <= first.End + AudioLabels.EdgeReach + 1);

    /// <summary>Advert stretches grouped into breaks: by a gap of up to a minute, or of up to <see cref="Bridge"/> in a break that opened on an edge clip, with no programme audio between.</summary>
    private static List<List<AudioSpan>> Groups(IReadOnlyList<AudioSpan> adverts, IReadOnlyList<AudioEdge> edges, IReadOnlyList<AudioSpan> programme)
    {
        var groups = new List<List<AudioSpan>>();
        foreach (var span in adverts)
        {
            if (groups.Count > 0 && Joins(groups[^1], span, edges, programme))
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

    private static bool Joins(List<AudioSpan> group, AudioSpan span, IReadOnlyList<AudioEdge> edges, IReadOnlyList<AudioSpan> programme)
    {
        var last = group[^1];
        var gap = span.Start - last.End;
        if (gap <= Gap)
        {
            return true;
        }

        if (gap > Bridge || !OpensOnEdgeClip(group[0], edges))
        {
            return false;
        }

        // The break as it would be published: from its implied start to the end edge the new stretch implies.
        var start = Nearest(edges, true, group[0].Start, group[0].Start - Gap, group[0].Start + 5) ?? group[0].Start;
        var end = Nearest(edges, false, span.End, span.End - 5, span.End + 30) ?? span.End;

        // Closed: an end edge at the last advert heard, past the opening clip, whose own old role may
        // put end edges at the very start. Where one bumper opens and closes every break, as on Sky,
        // the next break's opening clip must not reach back across the programme between. An end
        // edge with more adverts after it, within a minute, closed nothing: an advert that ended a
        // stored break may sit mid-break here.
        var closed = edges.Any(e => !e.IsStart && e.At >= last.End - 5 && e.At <= last.End + AudioLabels.EdgeReach + 1 && e.At - start >= MinClosed);

        // Programme audio anywhere since the opening clip: what follows it did not open on that clip.
        return !closed
            && end - start <= MaxBreak
            && !programme.Any(p => p.End - p.Start >= ProgrammeCut && p.Start < span.Start && p.End > group[0].Start);
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
            foreach (var p in programme.Where(p => p.End - p.Start >= ProgrammeCut))
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
