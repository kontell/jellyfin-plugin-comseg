using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Configuration;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Profiles;
using Jellyfin.Plugin.Comseg.Segments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Comskip;

/// <summary>
/// One full Comskip pass over a finished recording: compose the ini, run,
/// read the .edl, drop breaks under the minimum length, clean up. The
/// recording's channel profile, if one applies, adds its ini keys and may
/// change the analysis height. When the settings ask for a scaled copy,
/// Comskip reads a copy the GPU made instead of the recording: one a live job
/// made while it recorded, or one made here.
/// </summary>
public sealed class CompletedPass
{
    private readonly ComskipRunner _runner;
    private readonly Workspace _workspace;
    private readonly ScaledCopy _copies;
    private readonly ProfileSelector _profiles;
    private readonly ILogger<CompletedPass> _logger;

    /// <summary>Initializes a new instance of the <see cref="CompletedPass"/> class.</summary>
    /// <param name="runner">The runner.</param>
    /// <param name="workspace">The workspace.</param>
    /// <param name="copies">The scaled copies.</param>
    /// <param name="profiles">The channel profiles.</param>
    /// <param name="logger">The logger.</param>
    public CompletedPass(ComskipRunner runner, Workspace workspace, ScaledCopy copies, ProfileSelector profiles, ILogger<CompletedPass> logger)
    {
        _runner = runner;
        _workspace = workspace;
        _copies = copies;
        _profiles = profiles;
        _logger = logger;
    }

    /// <summary>Runs the pass.</summary>
    /// <param name="mediaPath">The recording.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="copy">A scaled copy of the finished recording made already, if there is one.</param>
    /// <returns>The breaks, or null when Comskip produced nothing.</returns>
    public async Task<IReadOnlyList<Break>?> RunAsync(string mediaPath, PluginConfiguration config, CancellationToken cancellationToken, string? copy = null)
    {
        var settings = _profiles.For(mediaPath, config);
        config = settings.Config;
        if (settings.Problems.Count > 0)
        {
            _logger.LogWarning("Comseg: profile {Profile} keys left out: {Problems}", settings.ProfileName, string.Join("; ", settings.Problems));
        }

        var jobDir = _workspace.JobDir(config, "scan", mediaPath);
        try
        {
            Directory.CreateDirectory(jobDir);
            var iniPath = Path.Combine(jobDir, "comskip.ini");
            var runMode = new Dictionary<string, string>(ComskipIni.CompletedOverrides(config), StringComparer.Ordinal);
            if (settings.KeepFrames)
            {
                foreach (var pair in ComskipIni.KeepFramesKeys)
                {
                    runMode[pair.Key] = pair.Value;
                }
            }

            await File.WriteAllTextAsync(
                iniPath,
                ComskipIni.Compose(ComskipIni.BaseText(config.IniPath), settings.Keys, runMode),
                cancellationToken).ConfigureAwait(false);

            // One time limit for the whole pass, the copy included, so a stuck
            // encode cannot double it.
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Clamp(config.CompletedPassTimeoutMinutes, 5, 24 * 60));
            copy ??= await _copies.PrepareAsync(mediaPath, Path.Combine(jobDir, "copy.ts"), config, deadline - DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                _logger.LogWarning("Comseg: the full pass over {Media} ran out of time making its copy", mediaPath);
                return null;
            }

            if (copy is null)
            {
                _logger.LogInformation("Comseg: full Comskip pass over {Media} ({Settings})", mediaPath, settings.Description);
            }
            else
            {
                _logger.LogInformation("Comseg: full Comskip pass over {Media}, through its scaled copy ({Settings})", mediaPath, settings.Description);
            }

            var input = copy ?? mediaPath;
            var result = await _runner.RunAsync(config.ComskipPath, iniPath, jobDir, input, left, cancellationToken).ConfigureAwait(false);
            if (!result.Ran || result.EdlPath is null)
            {
                return null;
            }

            if (settings.KeepFrames)
            {
                KeepFrames(jobDir, input, mediaPath, settings);
            }

            var min = Math.Max(0, config.MinBreakSeconds);
            var breaks = EdlParser.ReadFile(result.EdlPath).Where(b => b.Length >= min).ToList();
            _logger.LogInformation("Comseg: {Count} commercial breaks in {Media} after {Elapsed:mm\\:ss}", breaks.Count, mediaPath, result.Elapsed);
            return breaks;
        }
        finally
        {
            // A pass that stops early leaves its files too, and a scaled copy runs to gigabytes.
            Workspace.TryDelete(jobDir);
        }
    }

    /// <summary>
    /// Keeps Comskip's per-frame data, its logo, the ini and the breaks for a
    /// recording, under &lt;data&gt;/comseg/frames/&lt;hash of its path&gt;, replacing
    /// an earlier pass's. Comskip replays the .csv without decoding, which is
    /// what makes trying many settings on it cheap.
    /// </summary>
    private void KeepFrames(string jobDir, string input, string mediaPath, RunSettings settings)
    {
        var dir = _workspace.FramesDir(mediaPath);
        try
        {
            Workspace.TryDelete(dir);
            Directory.CreateDirectory(dir);
            var baseName = ComskipRunner.BaseName(input);
            foreach (var (from, to) in new[]
            {
                (baseName + ".csv", "frames.csv"),
                (baseName + ".logo.txt", "logo.txt"),
                (baseName + ".edl", "breaks.edl"),
                ("comskip.ini", "comskip.ini"),
            })
            {
                var source = Path.Combine(jobDir, from);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(dir, to), overwrite: true);
                }
            }

            File.WriteAllText(
                Path.Combine(dir, "about.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Path = mediaPath,
                    Channel = settings.Record?.ChannelName,
                    Profile = settings.ProfileName,
                    ReadFrom = input == mediaPath ? "the recording" : "a scaled copy",
                    KeptUtc = DateTime.UtcNow,
                }));
            _logger.LogInformation("Comseg: kept the per-frame data of {Media} in {Dir}", mediaPath, dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Comseg: could not keep the per-frame data of {Media}: {Problem}", mediaPath, ex.Message);
        }
    }
}
