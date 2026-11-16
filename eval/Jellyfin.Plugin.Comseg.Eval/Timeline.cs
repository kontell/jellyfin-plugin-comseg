using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Eval;

/// <summary>
/// The rows clients could see over the life of a recording: each entry is the
/// full set of rows in force from its time until the next entry's. Times are
/// seconds since the recording began, on the recording's own clock, so second
/// 600 of the programme is written at time 600.
/// </summary>
public sealed class Timeline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Initializes a new instance of the <see cref="Timeline"/> class.</summary>
    /// <param name="entries">The entries, in time order.</param>
    /// <param name="final">The rows after the final pass, or null when there was none.</param>
    /// <param name="finalAt">When the final rows replaced the live ones.</param>
    public Timeline(IReadOnlyList<Entry> entries, IReadOnlyList<Break>? final, double? finalAt)
    {
        Entries = entries;
        Final = final;
        FinalAt = final is null ? null : finalAt;
    }

    /// <summary>Gets the entries, in time order.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>Gets the rows after the final pass.</summary>
    public IReadOnlyList<Break>? Final { get; }

    /// <summary>Gets when the final rows replaced the live ones.</summary>
    public double? FinalAt { get; }

    /// <summary>The rows in force at a time.</summary>
    /// <param name="t">Seconds since the recording began.</param>
    /// <returns>The rows; none before the first entry.</returns>
    public IReadOnlyList<Break> At(double t)
    {
        if (Final is not null && FinalAt is { } at && t >= at)
        {
            return Final;
        }

        var lo = 0;
        var hi = Entries.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Entries[mid].T <= t)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found < 0 ? Array.Empty<Break>() : Entries[found].Rows;
    }

    /// <summary>Writes the timeline as JSON.</summary>
    /// <param name="path">The file.</param>
    public void Save(string path)
    {
        var dto = new Dto
        {
            Entries = Entries.Select(e => new EntryDto { T = e.T, Rows = Pairs(e.Rows) }).ToList(),
            Final = Final is null ? null : Pairs(Final),
            FinalAt = FinalAt,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions) + "\n");
    }

    /// <summary>Reads a timeline written by <see cref="Save"/>.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The timeline.</returns>
    public static Timeline Load(string path)
    {
        var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), JsonOptions) ?? throw new FormatException("empty timeline: " + path);
        return new Timeline(
            dto.Entries.Select(e => new Entry(e.T, Rows(e.Rows))).ToList(),
            dto.Final is null ? null : Rows(dto.Final),
            dto.FinalAt);
    }

    private static List<double[]> Pairs(IReadOnlyList<Break> rows) => rows.Select(r => new[] { r.Start, r.End }).ToList();

    private static IReadOnlyList<Break> Rows(List<double[]> pairs) => pairs.Select(p => new Break(p[0], p[1])).ToList();

    /// <summary>The rows in force from a time on.</summary>
    /// <param name="T">Seconds since the recording began.</param>
    /// <param name="Rows">The rows.</param>
    public sealed record Entry(double T, IReadOnlyList<Break> Rows);

    private sealed class Dto
    {
        public List<EntryDto> Entries { get; set; } = new();

        public List<double[]>? Final { get; set; }

        public double? FinalAt { get; set; }
    }

    private sealed class EntryDto
    {
        public double T { get; set; }

        public List<double[]> Rows { get; set; } = new();
    }
}
