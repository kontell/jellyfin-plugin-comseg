using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>
/// Listens to one recording as it grows: each new stretch of its fingerprint
/// is searched against the channel's stored recordings and against the
/// recording's own earlier part, whose breaks so far say what its repeats
/// are. A channel without history still learns within a recording: a
/// promo or advert heard in its first break is recognised in the next.
/// </summary>
public sealed class AudioListener
{
    /// <summary>How far back each search starts before the new audio, so that a match crossing the last search's end is found whole.</summary>
    public const double Overlap = 40;

    /// <summary>How long ago the recording's own audio must be to count as a repeat.</summary>
    public const double SelfGap = 20;

    private readonly IReadOnlyList<StoredRecording> _stored;
    private readonly List<uint> _items = [];
    private int _searched;
    private FingerprintIndex? _ownIndex;

    /// <summary>Initializes a new instance of the <see cref="AudioListener"/> class.</summary>
    /// <param name="stored">The channel's stored recordings.</param>
    public AudioListener(IReadOnlyList<StoredRecording> stored)
    {
        _stored = stored;
    }

    /// <summary>Gets the evidence so far.</summary>
    public AudioEvidence Evidence { get; } = new();

    /// <summary>Gets how much of the recording has been heard, in seconds.</summary>
    public double Heard => Fingerprint.TimeOf(_items.Count);

    /// <summary>Gets how many stored recordings it listens against.</summary>
    public int StoredCount => _stored.Count;

    /// <summary>Takes the recording's fingerprint from an item on, replacing what was held from there.</summary>
    /// <param name="from">The item the slice starts at.</param>
    /// <param name="items">The slice.</param>
    public void Hear(int from, ReadOnlySpan<uint> items)
    {
        if (from > _items.Count)
        {
            // A gap: pad with values that match nothing, so times stay true.
            _items.AddRange(new uint[from - _items.Count]);
        }

        _items.RemoveRange(from, _items.Count - from);
        foreach (var item in items)
        {
            _items.Add(item);
        }
    }

    /// <summary>Searches what has been heard since the last search.</summary>
    /// <param name="ownBreaks">The recording's own breaks so far, which label its repeats.</param>
    /// <param name="unsure">Stretches of the recording that may be breaks though they are not in <paramref name="ownBreaks"/>, Comskip's say: a repeat of audio first heard in one is not programme evidence.</param>
    public void Search(IReadOnlyList<Break> ownBreaks, IReadOnlyList<Break>? unsure = null)
    {
        var all = _items.ToArray();
        var from = Math.Max(0, _searched - Fingerprint.ItemAt(Overlap));
        if (all.Length - from < Matcher.MinItems)
        {
            return;
        }

        var window = all.AsSpan(from);
        var spans = new List<AudioSpan>();
        var edges = new List<AudioEdge>();
        foreach (var stored in _stored)
        {
            Collect(Matcher.Match(stored.Items, stored.Index, window), from, stored.Info.Breaks, [], spans, edges);
        }

        // The recording's own audio up to SelfGap before this window.
        var own = from - Fingerprint.ItemAt(SelfGap);
        if (own >= Matcher.MinItems && ownBreaks.Count > 0)
        {
            if (_ownIndex is null || own - _ownIndex.Length > Fingerprint.ItemAt(60))
            {
                _ownIndex = new FingerprintIndex(all, own);
            }

            var ownLength = _ownIndex.Length;
            Collect(Matcher.Match(all.AsSpan(0, ownLength), _ownIndex, window), from, ownBreaks, unsure ?? [], spans, edges);
        }

        Evidence.Add(spans, edges);
        _searched = all.Length;
    }

    private static void Collect(IReadOnlyList<MatchRun> runs, int from, IReadOnlyList<Break> breaks, IReadOnlyList<Break> unsure, List<AudioSpan> spans, List<AudioEdge> edges)
    {
        foreach (var run in runs)
        {
            var at = Fingerprint.TimeOf(run.AStart);
            var length = run.Length * Fingerprint.Hop;
            var (span, found) = AudioLabels.Label(Fingerprint.TimeOf(from + run.BStart), at, length, breaks);
            if (span is { } s && !(s.Kind == AudioKind.Programme && unsure.Any(u => u.Start < at + length && at < u.End)))
            {
                spans.Add(s);
            }

            edges.AddRange(found);
        }
    }
}
