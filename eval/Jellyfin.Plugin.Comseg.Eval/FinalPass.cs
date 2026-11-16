using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Configuration;
using Jellyfin.Plugin.Comseg.Edl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Comseg.Eval;

/// <summary>
/// The plugin's full pass over a finished file, run outside Jellyfin: the
/// same ini layering, the same Comskip invocation, the same shortest break.
/// The file may be a recording, a scaled copy of one, or the per-frame
/// <c>.csv</c> an earlier run kept, which Comskip replays without decoding.
/// </summary>
public static class FinalPass
{
    /// <summary>Composes the ini a run uses: the base, then the keys given, then the run-mode keys, which win.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="keys">Keys laid over the base ini, as a channel profile's would be.</param>
    /// <param name="runMode">The run-mode keys.</param>
    /// <returns>The ini text.</returns>
    public static string Ini(PluginConfiguration config, IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> runMode)
    {
        var layered = ComskipIni.Compose(ComskipIni.BaseText(config.IniPath), keys);
        return ComskipIni.Compose(layered, runMode);
    }

    /// <summary>Runs the pass.</summary>
    /// <param name="media">The file Comskip reads.</param>
    /// <param name="workDir">Where Comskip writes; kept afterwards.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="keys">Keys laid over the base ini.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The breaks, or null when Comskip did not produce a list.</returns>
    public static async Task<IReadOnlyList<Break>?> RunAsync(string media, string workDir, PluginConfiguration config, IReadOnlyDictionary<string, string> keys, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDir);
        var iniPath = Path.Combine(workDir, "comskip.ini");
        await File.WriteAllTextAsync(iniPath, Ini(config, keys, ComskipIni.CompletedOverrides(config)), cancellationToken).ConfigureAwait(false);
        var runner = new ComskipRunner(NullLogger<ComskipRunner>.Instance);
        var timeout = TimeSpan.FromMinutes(Math.Clamp(config.CompletedPassTimeoutMinutes, 5, 24 * 60));
        var result = await runner.RunAsync(config.ComskipPath, iniPath, workDir, media, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Ran || result.EdlPath is null)
        {
            Console.Error.WriteLine(result.Tail);
            return null;
        }

        var min = Math.Max(0, config.MinBreakSeconds);
        return EdlParser.ReadFile(result.EdlPath).Where(b => b.Length >= min).ToList();
    }
}
