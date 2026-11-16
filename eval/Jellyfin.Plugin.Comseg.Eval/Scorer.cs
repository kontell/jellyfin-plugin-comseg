using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Eval;

/// <summary>
/// Turns a timeline and a reference into one number, lower being better.
/// For a viewer watching a fixed time behind the recording's edge, a second
/// of the recording is skipped when a row covering it was in force by the
/// time that viewer reached it. Skipped programme is what a viewer loses and
/// counts five times; a second of a break left unskipped counts once. The
/// same is counted for the final rows. A row reaching into a stretch the
/// reference marks as seen to be uninterrupted programme fails the run.
/// </summary>
public static class Scorer
{
    /// <summary>The weight of a second of programme skipped against a second of adverts left in.</summary>
    public const double ProgrammeWeight = 5;

    /// <summary>What a row that is withdrawn, or whose start moves, costs.</summary>
    public const double ReplacementCost = 5;

    /// <summary>How far a row may reach into a free span before the run fails.</summary>
    public const double FreeTolerance = 5;

    /// <summary>How far a row's start may move and still be the same row.</summary>
    public const double SameRowTolerance = 1.5;

    private const double Step = 0.25;

    /// <summary>Scores a run.</summary>
    /// <param name="reference">What the recording holds.</param>
    /// <param name="timeline">What clients could see.</param>
    /// <param name="lags">How far behind the recording's edge each viewer is, in seconds.</param>
    /// <returns>The score and what it is made of.</returns>
    public static Result Score(Reference reference, Timeline timeline, IReadOnlyList<double> lags)
    {
        var viewers = new List<Viewer>();
        foreach (var lag in lags)
        {
            viewers.Add(Watch(reference, "live+" + lag.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s", t => timeline.At(t + lag)));
        }

        if (timeline.Final is { } final)
        {
            viewers.Add(Watch(reference, "final", _ => final));
        }

        var replacements = Replacements(timeline);
        var failures = Failures(reference, timeline);
        var total = viewers.Sum(v => (ProgrammeWeight * v.ProgrammeLost) + v.AdvertsLeft) + (ReplacementCost * replacements);
        return new Result(total, failures.Count > 0, viewers, replacements, failures);
    }

    private static Viewer Watch(Reference reference, string name, Func<double, IReadOnlyList<Break>> rowsAt)
    {
        double lost = 0, left = 0, skipped = 0;
        for (var p = 0.0; p < reference.Duration; p += Step)
        {
            if (reference.IsUnknown(p))
            {
                continue;
            }

            var inRow = Spans.Contains(rowsAt(p), p);
            var inBreak = reference.IsBreak(p);
            if (inRow && !inBreak)
            {
                lost += Step;
            }
            else if (!inRow && inBreak)
            {
                left += Step;
            }
            else if (inRow)
            {
                skipped += Step;
            }
        }

        return new Viewer(name, lost, left, skipped);
    }

    private static int Replacements(Timeline timeline)
    {
        var count = 0;
        for (var i = 1; i < timeline.Entries.Count; i++)
        {
            var now = timeline.Entries[i].Rows;
            count += timeline.Entries[i - 1].Rows.Count(old => !now.Any(r => Math.Abs(r.Start - old.Start) <= SameRowTolerance));
        }

        return count;
    }

    private static List<string> Failures(Reference reference, Timeline timeline)
    {
        var failures = new List<string>();
        var seen = new HashSet<(double Start, double End, double FreeStart)>();
        void Check(IReadOnlyList<Break> rows, string when)
        {
            foreach (var row in rows)
            {
                foreach (var free in reference.Free)
                {
                    var overlap = Spans.Overlap(row, free);
                    if (overlap > FreeTolerance && seen.Add((row.Start, row.End, free.Start)))
                    {
                        failures.Add(FormattableString.Invariant($"{when}: row {row.Start:0.0}-{row.End:0.0} covers {overlap:0.0} s of programme in {free.Start:0.0}-{free.End:0.0}"));
                    }
                }
            }
        }

        foreach (var entry in timeline.Entries)
        {
            Check(entry.Rows, FormattableString.Invariant($"live at {entry.T:0}"));
        }

        if (timeline.Final is { } final)
        {
            Check(final, "final");
        }

        return failures;
    }

    /// <summary>What one viewer got.</summary>
    /// <param name="Name">Which viewer.</param>
    /// <param name="ProgrammeLost">Seconds of programme skipped.</param>
    /// <param name="AdvertsLeft">Seconds of breaks not skipped.</param>
    /// <param name="AdvertsSkipped">Seconds of breaks skipped.</param>
    public sealed record Viewer(string Name, double ProgrammeLost, double AdvertsLeft, double AdvertsSkipped);

    /// <summary>A run's score.</summary>
    /// <param name="Total">The one number; lower is better.</param>
    /// <param name="Failed">Whether a row reached into a free span.</param>
    /// <param name="Viewers">Each viewer's seconds.</param>
    /// <param name="Replacements">Rows withdrawn or restarted.</param>
    /// <param name="Failures">Why the run failed, a line a row.</param>
    public sealed record Result(double Total, bool Failed, IReadOnlyList<Viewer> Viewers, int Replacements, IReadOnlyList<string> Failures);
}
