using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Live;

namespace Jellyfin.Plugin.Comseg.Eval;

/// <summary>
/// Replays the analyses a live Comskip wrote, in the order and at the times
/// they appeared, through the plugin's own decision about what to publish.
/// It polls as the watcher does: at each poll it reads the newest analysis,
/// if there is a new one, and publishes what the tracker then confirms.
/// </summary>
public static class LiveSim
{
    /// <summary>Reads a folder of analyses: <c>index.tsv</c> lists, one a line, the second of the recording at which an analysis appeared and the EDL file holding it.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The analyses, in time order.</returns>
    public static IReadOnlyList<Analysis> ReadAnalyses(string folder)
    {
        var list = new List<Analysis>();
        foreach (var raw in File.ReadAllLines(Path.Combine(folder, "index.tsv")))
        {
            var fields = raw.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 2 || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                continue;
            }

            list.Add(new Analysis(t, EdlParser.ReadFile(Path.Combine(folder, fields[1]))));
        }

        return list.OrderBy(a => a.T).ToList();
    }

    /// <summary>Runs the replay.</summary>
    /// <param name="analyses">The analyses, in time order.</param>
    /// <param name="pollSeconds">How often the watcher looks.</param>
    /// <param name="confirmPasses">Analyses a break must appear in.</param>
    /// <param name="minBreakSeconds">Shortest break published.</param>
    /// <param name="until">When the recording ended.</param>
    /// <returns>The entries of the timeline, without a final pass.</returns>
    public static IReadOnlyList<Timeline.Entry> Run(IReadOnlyList<Analysis> analyses, int pollSeconds, int confirmPasses, int minBreakSeconds, double until)
    {
        var tracker = new LiveBreakTracker(confirmPasses, minBreakSeconds);
        var entries = new List<Timeline.Entry>();
        IReadOnlyList<Break> published = Array.Empty<Break>();
        var next = 0;
        var poll = Math.Max(1, pollSeconds);
        for (double t = poll; t <= until + poll; t += poll)
        {
            var newest = -1;
            while (next < analyses.Count && analyses[next].T <= t)
            {
                newest = next++;
            }

            if (newest < 0)
            {
                continue;
            }

            tracker.Observe(analyses[newest].Breaks);
            if (!tracker.Confirmed.SequenceEqual(published))
            {
                published = tracker.Confirmed;
                entries.Add(new Timeline.Entry(t, published));
            }
        }

        return entries;
    }

    /// <summary>One analysis by the live Comskip.</summary>
    /// <param name="T">The second of the recording at which it appeared.</param>
    /// <param name="Breaks">What it reported.</param>
    public sealed record Analysis(double T, IReadOnlyList<Break> Breaks);
}
