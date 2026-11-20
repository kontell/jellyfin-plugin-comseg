using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Comseg.Audio;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Eval;

/// <summary>The evaluator's audio commands.</summary>
internal static class AudioCommands
{
    /// <summary>
    /// The audio two fingerprints share, or one shares with its own earlier
    /// part (--self), one run a line: start in A, start in B, length, bit error,
    /// in seconds, as the 2026-11-15 study's fpmatch.py printed them.
    /// </summary>
    /// <param name="a">The fingerprint searched against.</param>
    /// <param name="b">The fingerprint searched, or null with self.</param>
    /// <param name="self">Whether to search A against its own earlier part, 20 s back at least.</param>
    /// <returns>0.</returns>
    public static int Match(string a, string? b, bool self)
    {
        var fa = Fingerprint.Load(a);
        var fb = self ? fa : Fingerprint.Load(b ?? throw new ArgumentException("--b is required without --self"));
        var runs = Matcher.Match(fa, new FingerprintIndex(fa), fb, self ? Fingerprint.ItemAt(20) : int.MinValue);
        foreach (var run in runs.OrderBy(r => r.BStart))
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{Fingerprint.TimeOf(run.AStart),9:0.00} {Fingerprint.TimeOf(run.BStart),9:0.00} {run.Length * Fingerprint.Hop,7:0.00} {run.BitError:0.000}"));
        }

        return 0;
    }

    /// <summary>
    /// Replays a finished recording's fingerprint as it would have grown,
    /// poll by poll, through the listener and the shadow decision, with the
    /// channel's stored recordings (fingerprint and breaks) and, when given,
    /// the analyses a live Comskip wrote. Writes the timeline of rows the
    /// decision would have published, for score.
    /// </summary>
    /// <param name="fingerprint">The recording's fingerprint.</param>
    /// <param name="stored">The stored recordings, each FP:EDL.</param>
    /// <param name="analysesDir">A folder of analyses (index.tsv), or null for audio alone.</param>
    /// <param name="poll">Seconds between polls.</param>
    /// <param name="duration">Where to stop, or null for the fingerprint's end.</param>
    /// <param name="out">The timeline to write.</param>
    /// <param name="final">The final pass's EDL, or null.</param>
    /// <param name="finalAt">When the final rows take effect, or null.</param>
    /// <param name="minBreak">The shortest break published.</param>
    /// <param name="verbose">Whether to print the evidence too.</param>
    /// <returns>0.</returns>
    public static int Simulate(string fingerprint, IEnumerable<string> stored, string? analysesDir, int poll, double? duration, string @out, string? final, double? finalAt, int minBreak, bool verbose = false)
    {
        if (poll <= 0)
        {
            throw new ArgumentException("--poll must be at least 1");
        }

        var items = Fingerprint.Load(fingerprint);
        var store = stored.Select(spec =>
        {
            var (fp, edl) = Split(spec);
            var fpItems = Fingerprint.Load(fp);
            return new StoredRecording(
                new StoredRecordingInfo(fp, "eval", null, DateTime.UtcNow, Fingerprint.TimeOf(fpItems.Length), EdlParser.Parse(File.ReadAllText(edl))),
                fpItems);
        }).ToList();
        // Each stored recording's coverage against those listed before it, as the store records it when
        // it keeps one, oldest first: list the stored recordings in the order they aired.
        store = store.Select((s, i) => new StoredRecording(
            s.Info with { Coverage = AudioTrust.Coverage(s.Items, s.Info.Breaks, store.Take(i).ToList()) },
            s.Items)).ToList();
        var trust = AudioTrust.IsTrusted(store);
        Console.WriteLine("coverage " + string.Join(", ", store.Select(s => s.Info.Coverage?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-")) + (trust ? ": trusted" : ": not trusted"));
        var analyses = analysesDir is null ? [] : LiveSim.ReadAnalyses(analysesDir);
        var until = Math.Min(duration ?? double.MaxValue, Fingerprint.TimeOf(items.Length));

        var listener = new AudioListener(store);
        var entries = new List<Timeline.Entry>();
        IReadOnlyList<Break> shown = [];
        var heardTo = 0;
        for (var t = (double)poll; t <= until + poll; t += poll)
        {
            var now = Math.Min(t, until);
            var to = Math.Min(items.Length, Fingerprint.ItemAt(now));
            listener.Hear(heardTo, items.AsSpan(heardTo, to - heardTo));
            heardTo = to;

            // Its own breaks so far label its repeats: the rows shown, less any still open.
            listener.Search(shown.Where(b => b.End < now - 30).ToList());
            var seen = analyses.Where(a => a.T <= now).ToList();
            var latest = seen.Count > 0 ? seen[^1].Breaks : null;
            var previous = seen.Count > 1 ? seen[^2].Breaks : null;
            var hold = AudioShadow.HoldsBack(trust, seen.Count > 0 ? seen[^1].Status : null, store.Count);
            var rows = ShadowDecision.Decide(latest, previous, listener.Evidence, minBreak, hold, now).Select(r => r.Break).ToList();
            if (!rows.SequenceEqual(shown))
            {
                entries.Add(new Timeline.Entry(now, rows));
                shown = rows;
            }

            if (now >= until)
            {
                break;
            }
        }

        // As the watcher and live-sim do: a final pass that finds nothing keeps the live rows.
        IReadOnlyList<Break>? finalRows = final is null ? null : EdlParser.Parse(File.ReadAllText(final));
        if (finalRows is { Count: 0 })
        {
            finalRows = shown;
        }
        new Timeline(entries, finalRows, finalAt ?? until + 240).Save(@out);
        var adverts = listener.Evidence.Adverts;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{store.Count} stored; heard {until:0} s; advert audio {adverts.Sum(a => a.End - a.Start):0} s in {adverts.Count} stretches; programme audio {listener.Evidence.Programme.Sum(a => a.End - a.Start):0} s; {listener.Evidence.Edges.Count} edges; {entries.Count} changes of the rows"));
        foreach (var row in shown)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {row.Start,8:0.0} {row.End,8:0.0}"));
        }

        if (verbose)
        {
            foreach (var a in adverts)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  advert    {a.Start,8:0.0} {a.End,8:0.0}"));
            }

            foreach (var p in listener.Evidence.Programme)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  programme {p.Start,8:0.0} {p.End,8:0.0}"));
            }

            foreach (var e in listener.Evidence.Edges)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {(e.IsStart ? "start" : "end"),-9} {e.At,8:0.0}"));
            }
        }

        return 0;
    }

    private static (string Fp, string Edl) Split(string spec)
    {
        var colon = spec.LastIndexOf(':');
        return colon <= 0 ? throw new ArgumentException("--store takes FP:EDL, not " + spec) : (spec[..colon], spec[(colon + 1)..]);
    }
}
