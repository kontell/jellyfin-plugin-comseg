using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Eval;

/// <summary>
/// What a recording really holds, checked by eye. A text file, one fact a
/// line, times in seconds from the start of the recording:
/// <c>duration 10954</c>, <c>break 1247.2 1485.4</c>, <c>free 1500 2400</c>
/// for a stretch seen to be uninterrupted programme, <c>unknown 0 60</c> for
/// one nobody checked. Everything that is neither a break nor unknown counts
/// as programme; a free span is programme that was looked at from end to end,
/// and is where a published row fails a run outright.
/// </summary>
public sealed class Reference
{
    private Reference(double duration, IReadOnlyList<Break> breaks, IReadOnlyList<Break> free, IReadOnlyList<Break> unknown)
    {
        Duration = duration;
        Breaks = breaks;
        Free = free;
        Unknown = unknown;
    }

    /// <summary>Gets the recording's length.</summary>
    public double Duration { get; }

    /// <summary>Gets the breaks, sorted by start.</summary>
    public IReadOnlyList<Break> Breaks { get; }

    /// <summary>Gets the stretches seen to be uninterrupted programme.</summary>
    public IReadOnlyList<Break> Free { get; }

    /// <summary>Gets the stretches left out of scoring.</summary>
    public IReadOnlyList<Break> Unknown { get; }

    /// <summary>Reads a reference file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The reference.</returns>
    public static Reference Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses a reference.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The reference.</returns>
    public static Reference Parse(string text)
    {
        double? duration = null;
        var spans = new Dictionary<string, List<Break>>(StringComparer.Ordinal)
        {
            ["break"] = new(),
            ["free"] = new(),
            ["unknown"] = new(),
        };
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields[0] == "duration" && fields.Length == 2 && Seconds(fields[1]) is { } d)
            {
                duration = d;
            }
            else if (spans.TryGetValue(fields[0], out var list) && fields.Length == 3 && Seconds(fields[1]) is { } start && Seconds(fields[2]) is { } end && end > start)
            {
                list.Add(new Break(start, end));
            }
            else
            {
                throw new FormatException("reference line " + number.ToString(CultureInfo.InvariantCulture) + " is not understood: " + line);
            }
        }

        if (duration is not { } length || length <= 0)
        {
            throw new FormatException("the reference has no duration line");
        }

        return new Reference(length, Sorted(spans["break"]), Sorted(spans["free"]), Sorted(spans["unknown"]));
    }

    /// <summary>Whether a moment is inside a break.</summary>
    /// <param name="t">Seconds from the start.</param>
    /// <returns>True inside a break.</returns>
    public bool IsBreak(double t) => Spans.Contains(Breaks, t);

    /// <summary>Whether a moment is left out of scoring.</summary>
    /// <param name="t">Seconds from the start.</param>
    /// <returns>True inside an unknown span.</returns>
    public bool IsUnknown(double t) => Spans.Contains(Unknown, t);

    private static IReadOnlyList<Break> Sorted(List<Break> list) => list.OrderBy(b => b.Start).ToList();

    private static double? Seconds(string field)
        => double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
}
