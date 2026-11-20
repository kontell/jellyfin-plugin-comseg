using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>What a stretch of a recording's audio was heard as elsewhere.</summary>
public enum AudioKind
{
    /// <summary>Heard inside breaks: an advert, a promo, a sponsor clip.</summary>
    Advert,

    /// <summary>Heard outside breaks: a theme, a sting, a studio jingle.</summary>
    Programme,
}

/// <summary>A stretch of the recording heard elsewhere, in seconds of the recording.</summary>
/// <param name="Start">Start.</param>
/// <param name="End">End.</param>
/// <param name="Kind">What it was heard as.</param>
public readonly record struct AudioSpan(double Start, double End, AudioKind Kind);

/// <summary>Where audio heard elsewhere says a break starts or ends, in seconds of the recording.</summary>
/// <param name="At">The edge.</param>
/// <param name="IsStart">True for a start, false for an end.</param>
public readonly record struct AudioEdge(double At, bool IsStart);

/// <summary>
/// The audio evidence about one recording, gathered as it grows: stretches
/// heard as adverts or as programme, and the break edges they imply.
/// Stretches of one kind that overlap are joined; where advert and programme
/// evidence overlap, neither is trusted there.
/// </summary>
public sealed class AudioEvidence
{
    private readonly List<AudioSpan> _adverts = [];
    private readonly List<AudioSpan> _programme = [];
    private readonly List<AudioEdge> _edges = [];

    /// <summary>Gets the stretches heard only as adverts, in order, conflicts removed.</summary>
    public IReadOnlyList<AudioSpan> Adverts => Without(_adverts, _programme);

    /// <summary>Gets the stretches heard only as programme, in order, conflicts removed.</summary>
    public IReadOnlyList<AudioSpan> Programme => Without(_programme, _adverts);

    /// <summary>Gets the edges, in order.</summary>
    public IReadOnlyList<AudioEdge> Edges => _edges.OrderBy(e => e.At).ToList();

    /// <summary>Adds what one search found.</summary>
    /// <param name="spans">The stretches.</param>
    /// <param name="edges">The edges.</param>
    public void Add(IEnumerable<AudioSpan> spans, IEnumerable<AudioEdge> edges)
    {
        foreach (var span in spans)
        {
            Join(span.Kind == AudioKind.Advert ? _adverts : _programme, span);
        }

        foreach (var edge in edges)
        {
            // The same edge is found again by every later search over it.
            if (!_edges.Any(e => e.IsStart == edge.IsStart && Math.Abs(e.At - edge.At) < 2))
            {
                _edges.Add(edge);
            }
        }
    }

    private static void Join(List<AudioSpan> list, AudioSpan span)
    {
        var start = span.Start;
        var end = span.End;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Start <= end + 1 && start <= list[i].End + 1)
            {
                start = Math.Min(start, list[i].Start);
                end = Math.Max(end, list[i].End);
                list.RemoveAt(i);
            }
        }

        list.Add(new AudioSpan(start, end, span.Kind));
        list.Sort((x, y) => x.Start.CompareTo(y.Start));
    }

    /// <summary>The stretches of one kind with the other kind's cut out.</summary>
    private static List<AudioSpan> Without(List<AudioSpan> keep, List<AudioSpan> cut)
    {
        var result = new List<AudioSpan>();
        foreach (var span in keep)
        {
            var pieces = new List<(double S, double E)> { (span.Start, span.End) };
            foreach (var c in cut)
            {
                pieces = pieces.SelectMany(p => c.End <= p.S || c.Start >= p.E
                    ? new[] { p }
                    : new[] { (p.S, c.Start), (c.End, p.E) }.Where(q => q.Item2 - q.Item1 > 0)).ToList();
            }

            result.AddRange(pieces.Where(p => p.E - p.S >= 2).Select(p => new AudioSpan(p.S, p.E, span.Kind)));
        }

        return result;
    }
}

/// <summary>Turns matches against stored recordings into evidence.</summary>
public static class AudioLabels
{
    /// <summary>A match at least this far inside stored breaks is advert audio.</summary>
    public const double AdvertShare = 0.8;

    /// <summary>A match at most this far inside stored breaks is programme audio.</summary>
    public const double ProgrammeShare = 0.2;

    /// <summary>How far into a stored break a match may start and still say where the break starts (or end before its end).</summary>
    public const double EdgeReach = 15;

    /// <summary>
    /// What one match says about the recording: the stretch it covers, as
    /// advert or programme audio by where it fell in the stored recording, and
    /// the break edges it implies. A match that crosses a stored break's edge
    /// puts the recording's edge at the same place in the match; one that
    /// starts just inside a stored break, an opening sponsor clip say, puts
    /// the recording's break start as far before it.
    /// </summary>
    /// <param name="liveStart">Where the match starts in the recording, seconds.</param>
    /// <param name="storedStart">Where it starts in the stored recording, seconds.</param>
    /// <param name="length">Its length, seconds.</param>
    /// <param name="storedBreaks">The stored recording's breaks.</param>
    /// <returns>The stretch, if it has a kind, and the edges.</returns>
    public static (AudioSpan? Span, IReadOnlyList<AudioEdge> Edges) Label(double liveStart, double storedStart, double length, IReadOnlyList<Break> storedBreaks)
    {
        var storedEnd = storedStart + length;
        var inside = storedBreaks.Sum(b => Math.Max(0, Math.Min(b.End, storedEnd) - Math.Max(b.Start, storedStart)));
        var share = inside / length;
        AudioSpan? span = share >= AdvertShare
            ? new AudioSpan(liveStart, liveStart + length, AudioKind.Advert)
            : share <= ProgrammeShare ? new AudioSpan(liveStart, liveStart + length, AudioKind.Programme) : null;

        var edges = new List<AudioEdge>();
        foreach (var b in storedBreaks)
        {
            // An edge inside the match: the most exact evidence there is.
            if (b.Start > storedStart + 1 && b.Start < storedEnd - 1)
            {
                edges.Add(new AudioEdge(liveStart + (b.Start - storedStart), true));
            }
            else if (storedStart - b.Start is >= 0 and <= EdgeReach && storedStart < b.End)
            {
                edges.Add(new AudioEdge(liveStart - (storedStart - b.Start), true));
            }

            if (b.End > storedStart + 1 && b.End < storedEnd - 1)
            {
                edges.Add(new AudioEdge(liveStart + (b.End - storedStart), false));
            }
            else if (b.End - storedEnd is >= 0 and <= EdgeReach && storedEnd > b.Start)
            {
                edges.Add(new AudioEdge(liveStart + length + (b.End - storedEnd), false));
            }
        }

        return (span, edges);
    }
}
