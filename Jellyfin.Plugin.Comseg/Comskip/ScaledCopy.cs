using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Comskip;

/// <summary>
/// A copy of a recording scaled down on the GPU for Comskip to analyse.
/// Comskip decodes in software, and a UHD recording costs it several cores
/// and about half the recording's length for a full pass; through VA-API the
/// GPU decodes and scales the same recording for a fraction of a core, and
/// Comskip reads the small copy many times faster. The copy is made with
/// Jellyfin's own ffmpeg, which has VA-API built in, keeps every audio track
/// (Comskip listens for silences) and keeps the source's timestamps.
/// Comskip times a file from its first whole frame, which is the copy's
/// first frame too, so breaks found in the copy fall at the same times as
/// in the recording: on a 3 h 40 min game all 27 breaks matched, most of
/// their starts to the hundredth of a second.
///
/// Jellyfin's media encoder is resolved per call: the full pass that uses
/// this class belongs to the segment provider, which must not take
/// Jellyfin's managers in its constructor graph.
/// </summary>
public sealed class ScaledCopy
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ScaledCopy> _logger;

    /// <summary>Initializes a new instance of the <see cref="ScaledCopy"/> class.</summary>
    /// <param name="services">The service provider, for Jellyfin's media encoder.</param>
    /// <param name="logger">The logger.</param>
    public ScaledCopy(IServiceProvider services, ILogger<ScaledCopy> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>The height to analyse at, or 0 to analyse recordings as they are.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The height.</returns>
    public static int Height(PluginConfiguration config)
        => config.AnalysisHeight > 0 ? Math.Clamp(config.AnalysisHeight, 240, 1080) : 0;

    /// <summary>
    /// Starts a copy that follows a recording still being written. Like the
    /// live Comskip it waits out a stalled recording for the stall tolerance
    /// before it ends; when the recording ends the job stops it once it has
    /// caught up.
    /// </summary>
    /// <param name="source">The recording.</param>
    /// <param name="copy">The copy to write.</param>
    /// <param name="height">The height to scale to.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The running copy, or null when ffmpeg could not be started.</returns>
    public Following? StartFollowing(string source, string copy, int height, PluginConfiguration config)
    {
        var stall = TimeSpan.FromSeconds(4.0 * Math.Clamp(config.LiveTvRetries, 1, 100000));
        var process = Start(source, copy, height, config, stall, out var tail);
        return process is null ? null : new Following(process, tail, copy);
    }

    /// <summary>
    /// Copies a finished recording when a copy is wanted and the recording is
    /// taller than the height asked for; null when the recording is to be
    /// analysed as it is, including when the copy fails.
    /// </summary>
    /// <param name="source">The recording.</param>
    /// <param name="copy">The copy to write.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="timeout">How long the copy may take.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The copy, or null.</returns>
    public async Task<string?> PrepareAsync(string source, string copy, PluginConfiguration config, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var height = Height(config);
        if (height == 0)
        {
            return null;
        }

        var original = await ProbeAsync(source, cancellationToken).ConfigureAwait(false);
        if (original is null || original.Height <= height)
        {
            return null;
        }

        var started = DateTime.UtcNow;
        using var process = Start(source, copy, height, config, null, out var tail);
        if (process is null)
        {
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ComskipRunner.TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning("Comseg: the {Height}p copy of {Media} timed out after {Timeout}; analysing the recording itself", height, source, timeout);
            return null;
        }

        var scaled = process.ExitCode == 0 ? await ProbeAsync(copy, cancellationToken).ConfigureAwait(false) : null;
        if (scaled is null || scaled.Duration < original.Duration - 10)
        {
            _logger.LogWarning("Comseg: the {Height}p copy of {Media} failed (code {Code}); analysing the recording itself. {Tail}", height, source, process.ExitCode, tail);
            return null;
        }

        _logger.LogInformation("Comseg: made a {Height}p copy of {Media} on the GPU in {Elapsed:mm\\:ss}", height, source, DateTime.UtcNow - started);
        return copy;
    }

    /// <summary>A file's picture height and duration, through Jellyfin's ffprobe.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The facts, or null when the file has no readable video yet.</returns>
    public async Task<Probe?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = _services.GetRequiredService<IMediaEncoder>().ProbePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=height", "-show_entries", "format=duration", "-of", "default=nw=1", path })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        string output;
        try
        {
            process.Start();
            var read = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            output = await read.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ComskipRunner.TryKill(process);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Comseg: ffprobe could not be started");
            return null;
        }

        var duration = double.NaN;
        var height = 0;
        foreach (var line in output.Split('\n'))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "height":
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                    break;
                case "duration":
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                    break;
            }
        }

        return height == 0 ? null : new Probe(height, double.IsNaN(duration) ? 0 : duration);
    }

    /// <summary>The ffmpeg arguments for a copy.</summary>
    /// <param name="source">The recording.</param>
    /// <param name="copy">The copy to write.</param>
    /// <param name="height">The height to scale to; a smaller picture is not scaled up.</param>
    /// <param name="device">The VA-API render device.</param>
    /// <param name="follow">For a file still being written, how long to wait at its end for more; null for a finished file.</param>
    /// <returns>The arguments.</returns>
    internal static IReadOnlyList<string> Arguments(string source, string copy, int height, string device, TimeSpan? follow)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error", "-y" };
        if (follow is { } wait)
        {
            // Read on at the end of the file, and end once it has not grown for that long.
            args.AddRange(new[] { "-follow", "1", "-rw_timeout", ((long)wait.TotalMicroseconds).ToString(CultureInfo.InvariantCulture) });
        }

        args.AddRange(new[]
        {
            "-hwaccel", "vaapi", "-hwaccel_device", device, "-hwaccel_output_format", "vaapi",
            "-i", source,
            "-map", "0:v:0", "-map", "0:a?",
            "-vf", string.Format(CultureInfo.InvariantCulture, "scale_vaapi=w=-2:h='min({0},ih)':format=nv12", height),
            // A coarse encode: at 720p about 2.8 Mbit/s on a sports feed, and finer
            // ones found the same breaks in a larger file.
            "-c:v", "h264_vaapi", "-qp", "30",
            "-c:a", "copy",
            "-copyts",
            "-f", "mpegts", copy,
        });
        return args;
    }

    private Process? Start(string source, string copy, int height, PluginConfiguration config, TimeSpan? follow, out OutputTail tail)
    {
        tail = new OutputTail();
        var info = new ProcessStartInfo
        {
            FileName = _services.GetRequiredService<IMediaEncoder>().EncoderPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var device = string.IsNullOrWhiteSpace(config.VaapiDevice) ? "/dev/dri/renderD128" : config.VaapiDevice.Trim();
        foreach (var arg in Arguments(source, copy, height, device, follow))
        {
            info.ArgumentList.Add(arg);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var lines = tail;
        process.OutputDataReceived += (_, e) => lines.Add(e.Data);
        process.ErrorDataReceived += (_, e) => lines.Add(e.Data);
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Comseg: ffmpeg could not be started from {Path} for a scaled copy", info.FileName);
            process.Dispose();
            return null;
        }
    }

    /// <summary>A file's picture height and duration.</summary>
    /// <param name="Height">The picture height.</param>
    /// <param name="Duration">The duration, seconds; 0 when unknown.</param>
    public sealed record Probe(int Height, double Duration);

    /// <summary>A copy being made from a recording still being written.</summary>
    public sealed class Following : IDisposable
    {
        private readonly Process _process;
        private readonly OutputTail _tail;
        private readonly string _path;

        internal Following(Process process, OutputTail tail, string path)
        {
            _process = process;
            _tail = tail;
            _path = path;
        }

        /// <summary>Gets a value indicating whether ffmpeg has exited.</summary>
        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        /// <summary>Gets the exit code, or null while running.</summary>
        public int? ExitCode => HasExited ? _process.ExitCode : null;

        /// <summary>Gets the last part of ffmpeg's output.</summary>
        public string Tail => _tail.ToString();

        /// <summary>
        /// Once the recording has ended, waits for the copy to catch up, which
        /// shows as its file no longer growing while ffmpeg waits at the
        /// recording's end, and then stops ffmpeg.
        /// </summary>
        /// <param name="limit">How long to wait at most.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>A task.</returns>
        public async Task FinishAsync(TimeSpan limit, CancellationToken cancellationToken)
        {
            var step = TimeSpan.FromSeconds(2);
            var deadline = DateTime.UtcNow + limit;
            var still = TimeSpan.Zero;
            long last = -1;
            while (!HasExited && DateTime.UtcNow < deadline && still < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(step, cancellationToken).ConfigureAwait(false);
                long size;
                try
                {
                    size = new FileInfo(_path).Length;
                }
                catch (IOException)
                {
                    size = -1;
                }

                still = size == last ? still + step : TimeSpan.Zero;
                last = size;
            }

            ComskipRunner.TryKill(_process);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            ComskipRunner.TryKill(_process);
            _process.Dispose();
        }
    }
}
