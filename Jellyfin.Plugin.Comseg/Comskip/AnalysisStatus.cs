using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Comskip;

/// <summary>
/// What the Comseg build of Comskip says about an analysis, in the
/// <c>&lt;base&gt;.master.status</c> it writes beside each periodic analysis
/// (kontell/Comskip#6): how far it read, whether it had the channel logo
/// and where from, the logo's share of frames, and each break's score.
/// </summary>
/// <param name="Seconds">How far the analysis read.</param>
/// <param name="Logo">none, found, loaded or disabled.</param>
/// <param name="LogoFraction">The share of frames showing the logo.</param>
/// <param name="Breaks">The breaks with their scores.</param>
public sealed record AnalysisStatus(double Seconds, string Logo, double LogoFraction, IReadOnlyList<(Break Break, double Score)> Breaks)
{
    /// <summary>Gets a value indicating whether the analysis was made without the channel logo, on the picture's other cues alone.</summary>
    public bool WithoutLogo => Logo is "none" or "disabled";

    /// <summary>
    /// Reads a status file. None there means a build that does not write one:
    /// null. One there that cannot be read or makes no sense gives the last
    /// status known instead, so that a failed read does not lift what the last
    /// one said.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="last">The last status read, or null.</param>
    /// <returns>The status.</returns>
    public static AnalysisStatus? Read(string path, AnalysisStatus? last = null)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) ?? last : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return last;
        }
    }

    /// <summary>Parses a status file's text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The status, or null when it names no logo state.</returns>
    public static AnalysisStatus? Parse(string text)
    {
        double seconds = 0, fraction = 0;
        string? logo = null;
        var breaks = new List<(Break, double)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var value = line[(eq + 1)..].Trim();
            switch (line[..eq])
            {
                case "seconds":
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                    break;
                case "logo":
                    logo = value;
                    break;
                case "logo_fraction":
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fraction);
                    break;
                case "break":
                    var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3
                        && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var e)
                        && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                    {
                        breaks.Add((new Break(s, e), score));
                    }

                    break;
            }
        }

        return logo is null ? null : new AnalysisStatus(seconds, logo, fraction, breaks);
    }
}
