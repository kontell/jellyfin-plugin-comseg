using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Configuration;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Eval;

/// <summary>The evaluator's command line.</summary>
public static class Program
{
    private const string Usage = """
        comseg-eval final    --media FILE --out DIR [--comskip PATH] [--ini FILE] [--set KEY=VALUE]...
                             [--min-break SECONDS] [--threads N] [--keep-frames]
        comseg-eval live-sim --analyses DIR --out FILE [--poll SECONDS] [--confirm N] [--min-break SECONDS]
                             [--duration SECONDS] [--final EDL] [--final-at SECONDS]
        comseg-eval score    --reference FILE --timeline FILE [--lags 30,120,600,1800] [--json]
        comseg-eval audio-match --a A.fp (--b B.fp | --self)
        comseg-eval audio-sim --fp FILE.fp --out FILE [--store FP:EDL]... [--analyses DIR] [--poll SECONDS]
                             [--duration SECONDS] [--final EDL] [--final-at SECONDS] [--min-break SECONDS]

        final     runs the plugin's full pass over a file (a recording, a scaled copy, or a kept
                  per-frame .csv) and writes DIR/final.edl.
        live-sim  replays the analyses a live Comskip wrote (DIR/index.tsv: second of the recording,
                  tab, EDL file) through the plugin's live decision and writes the timeline of rows.
        score     compares a timeline with a reference; lower is better, exit code 2 when a row
                  reaches into programme the reference marks as free of breaks.
        audio-match  the audio two raw Chromaprint fingerprints share (or one shares with its own
                  earlier part): start in A, start in B, length, bit error.
        audio-sim  replays a recording's fingerprint as it grew through the audio evidence and the
                  shadow decision, against stored recordings and, given, a live Comskip's analyses;
                  writes the timeline of rows for score.
        """;

    /// <summary>Runs a command.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>0 on success, 1 on a usage or run error, 2 when a scored run fails.</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        try
        {
            var options = new Options(args.Skip(1).ToArray());
            return args[0] switch
            {
                "final" => await FinalAsync(options).ConfigureAwait(false),
                "live-sim" => LiveSimulation(options),
                "score" => Score(options),
                "audio-match" => AudioCommands.Match(options.Required("a"), options.One("b"), options.Flag("self")),
                "audio-sim" => AudioCommands.Simulate(
                    options.Required("fp"),
                    options.All("store"),
                    options.One("analyses"),
                    options.Int("poll") ?? 15,
                    options.Number("duration"),
                    options.Required("out"),
                    options.One("final"),
                    options.Number("final-at"),
                    options.Int("min-break") ?? 20,
                    options.Flag("verbose")),
                _ => throw new ArgumentException("unknown command: " + args[0]),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            Console.Error.WriteLine("comseg-eval: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> FinalAsync(Options options)
    {
        var config = new PluginConfiguration
        {
            ComskipPath = options.One("comskip") ?? "comskip",
            IniPath = options.One("ini") ?? string.Empty,
            MinBreakSeconds = options.Int("min-break") ?? 20,
            ThreadCount = options.Int("threads") ?? 4,
        };
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.Flag("keep-frames"))
        {
            keys["output_framearray"] = "1";
            keys["delete_logo_file"] = "0";
        }

        foreach (var pair in options.All("set"))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new ArgumentException("--set takes KEY=VALUE, not " + pair);
            }

            keys[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }

        var outDir = options.Required("out");
        var breaks = await FinalPass.RunAsync(options.Required("media"), outDir, config, keys, CancellationToken.None).ConfigureAwait(false);
        if (breaks is null)
        {
            Console.Error.WriteLine("comseg-eval: Comskip wrote no break list");
            return 1;
        }

        File.WriteAllLines(Path.Combine(outDir, "final.edl"), breaks.Select(Line));
        foreach (var b in breaks)
        {
            Console.WriteLine(Line(b));
        }

        return 0;
    }

    private static int LiveSimulation(Options options)
    {
        var analyses = LiveSim.ReadAnalyses(options.Required("analyses"));
        if (analyses.Count == 0)
        {
            throw new ArgumentException("no analyses listed in index.tsv");
        }

        var duration = options.Number("duration") ?? analyses[^1].T;
        var entries = LiveSim.Run(analyses, options.Int("poll") ?? 15, options.Int("confirm") ?? 2, options.Int("min-break") ?? 20, duration);
        IReadOnlyList<Break>? final = null;
        if (options.One("final") is { } finalPath)
        {
            // As the watcher does: a final pass that finds nothing keeps the live rows.
            final = EdlParser.Parse(File.ReadAllText(finalPath));
            if (final.Count == 0 && entries.Count > 0)
            {
                final = entries[^1].Rows;
            }
        }

        var timeline = new Timeline(entries, final, options.Number("final-at") ?? duration + 240);
        timeline.Save(options.Required("out"));
        Console.WriteLine(FormattableString.Invariant($"{analyses.Count} analyses, {entries.Count} changes of the published rows"));
        return 0;
    }

    private static int Score(Options options)
    {
        var reference = Reference.Load(options.Required("reference"));
        var timeline = Timeline.Load(options.Required("timeline"));
        var lags = (options.One("lags") ?? "30,120,600,1800")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
            .ToList();
        var result = Scorer.Score(reference, timeline, lags);
        if (options.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        else
        {
            Console.WriteLine(FormattableString.Invariant($"score {result.Total:0.0}{(result.Failed ? "  FAILED" : string.Empty)}"));
            Console.WriteLine("viewer        programme lost  adverts left  adverts skipped");
            foreach (var v in result.Viewers)
            {
                Console.WriteLine(FormattableString.Invariant($"{v.Name,-12}  {v.ProgrammeLost,14:0.0}  {v.AdvertsLeft,12:0.0}  {v.AdvertsSkipped,15:0.0}"));
            }

            Console.WriteLine(FormattableString.Invariant($"rows withdrawn or restarted: {result.Replacements}"));
            foreach (var failure in result.Failures)
            {
                Console.WriteLine("fail: " + failure);
            }
        }

        return result.Failed ? 2 : 0;
    }

    private static string Line(Break b) => FormattableString.Invariant($"{b.Start:0.00}\t{b.End:0.00}\t0");

    private sealed class Options
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

        public Options(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("unexpected argument: " + args[i]);
                }

                var name = args[i][2..];
                if (!_values.TryGetValue(name, out var list))
                {
                    _values[name] = list = new List<string>();
                }

                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    list.Add(args[++i]);
                }
            }
        }

        public bool Flag(string name) => _values.ContainsKey(name);

        public IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var list) ? list : Array.Empty<string>();

        public string? One(string name) => All(name).LastOrDefault();

        public string Required(string name) => One(name) ?? throw new ArgumentException("--" + name + " is required");

        public int? Int(string name) => One(name) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;

        public double? Number(string name) => One(name) is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : null;
    }
}
