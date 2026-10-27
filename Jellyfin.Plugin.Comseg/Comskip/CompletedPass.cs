using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Configuration;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Segments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Comskip;

/// <summary>
/// One full Comskip pass over a finished recording: compose the ini, run,
/// read the .edl, drop breaks under the minimum length, clean up.
/// </summary>
public sealed class CompletedPass
{
    private readonly ComskipRunner _runner;
    private readonly Workspace _workspace;
    private readonly ILogger<CompletedPass> _logger;

    /// <summary>Initializes a new instance of the <see cref="CompletedPass"/> class.</summary>
    /// <param name="runner">The runner.</param>
    /// <param name="workspace">The workspace.</param>
    /// <param name="logger">The logger.</param>
    public CompletedPass(ComskipRunner runner, Workspace workspace, ILogger<CompletedPass> logger)
    {
        _runner = runner;
        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>Runs the pass.</summary>
    /// <param name="mediaPath">The recording.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The breaks, or null when Comskip produced nothing.</returns>
    public async Task<IReadOnlyList<Break>?> RunAsync(string mediaPath, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var jobDir = _workspace.JobDir(config, "scan", mediaPath);
        Directory.CreateDirectory(jobDir);
        var iniPath = Path.Combine(jobDir, "comskip.ini");
        await File.WriteAllTextAsync(
            iniPath,
            ComskipIni.Compose(ComskipIni.BaseText(config.IniPath), ComskipIni.CompletedOverrides(config)),
            cancellationToken).ConfigureAwait(false);

        var timeout = TimeSpan.FromMinutes(Math.Clamp(config.CompletedPassTimeoutMinutes, 5, 24 * 60));
        _logger.LogInformation("Comseg: full Comskip pass over {Media}", mediaPath);
        var result = await _runner.RunAsync(config.ComskipPath, iniPath, jobDir, mediaPath, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Ran || result.EdlPath is null)
        {
            return null;
        }

        var min = Math.Max(0, config.MinBreakSeconds);
        var breaks = EdlParser.ReadFile(result.EdlPath).Where(b => b.Length >= min).ToList();
        _logger.LogInformation("Comseg: {Count} commercial breaks in {Media} after {Elapsed:mm\\:ss}", breaks.Count, mediaPath, result.Elapsed);
        Workspace.TryDelete(jobDir);
        return breaks;
    }
}
