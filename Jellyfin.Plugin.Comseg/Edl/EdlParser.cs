using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Comseg.Edl;

/// <summary>
/// Reads Comskip's .edl: one break per line, "start end type" in seconds,
/// tab or space separated. Tolerant of a torn last line (the live file is
/// truncated and rewritten) and of anything that is not three fields.
/// </summary>
public static class EdlParser
{
    /// <summary>Parses EDL text.</summary>
    /// <param name="text">The file contents.</param>
    /// <returns>Breaks sorted by start, with non-positive lengths dropped.</returns>
    public static IReadOnlyList<Break> Parse(string text)
    {
        var breaks = new List<Break>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
            {
                continue;
            }

            if (end <= start || start < 0)
            {
                continue;
            }

            breaks.Add(new Break(start, end));
        }

        return breaks.OrderBy(b => b.Start).ToList();
    }

    /// <summary>Reads and parses a file, sharing it with a writer; an unreadable file is an empty list.</summary>
    /// <param name="path">The .edl path.</param>
    /// <returns>The breaks.</returns>
    public static IReadOnlyList<Break> ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (IOException)
        {
            return Array.Empty<Break>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<Break>();
        }
    }
}
