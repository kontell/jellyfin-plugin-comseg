using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Comskip;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>
/// Fingerprints a recording's first audio track with Jellyfin's own ffmpeg,
/// whose chromaprint muxer writes the raw items to a pipe once its input
/// ends: a whole finished recording in a few seconds an hour, or the newest
/// stretch of one still being written in a fraction of a second. One
/// extractor everywhere, because fpcalc and ffmpeg's muxer disagree on 12 to
/// 22% of items for the same file.
/// </summary>
public sealed class Fingerprinter
{
    private readonly Func<string> _ffmpeg;
    private readonly ILogger _logger;
    private bool? _available;

    /// <summary>Initializes a new instance of the <see cref="Fingerprinter"/> class.</summary>
    /// <param name="services">The service provider, for the media encoder (resolved per call; see ComsegSegmentProvider).</param>
    /// <param name="logger">The logger.</param>
    public Fingerprinter(IServiceProvider services, ILogger<Fingerprinter> logger)
        : this(() => services.GetRequiredService<IMediaEncoder>().EncoderPath, logger)
    {
    }

    private Fingerprinter(Func<string> ffmpeg, ILogger logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>A fingerprinter with a given ffmpeg, for tools outside Jellyfin.</summary>
    /// <param name="ffmpeg">The ffmpeg binary.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The fingerprinter.</returns>
    public static Fingerprinter WithFfmpeg(string ffmpeg, ILogger logger) => new(() => ffmpeg, logger);

    /// <summary>Whether the server's ffmpeg has the chromaprint muxer; asked once, and said in the log when not.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when it has.</returns>
    public async Task<bool> AvailableAsync(CancellationToken cancellationToken)
    {
        if (_available is { } known)
        {
            return known;
        }

        var (code, output) = await RunAsync(["-hide_banner", "-muxers"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var text = System.Text.Encoding.UTF8.GetString(output);
        _available = code == 0 && text.Contains(" chromaprint ", StringComparison.Ordinal);
        if (_available == false)
        {
            _logger.LogWarning("Comseg: the server's ffmpeg has no chromaprint muxer, so audio evidence is off");
        }

        return _available.Value;
    }

    /// <summary>The fingerprint of a recording, or of a stretch of one.</summary>
    /// <param name="path">The recording.</param>
    /// <param name="from">Where to start, in seconds; the items line up with the whole recording's from item round(from / hop).</param>
    /// <param name="seconds">How much to take, or null for the rest of the file as it stands.</param>
    /// <param name="timeout">How long ffmpeg may take before it is killed and the extraction fails.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The items, or null when ffmpeg failed or ran out of time.</returns>
    public async Task<uint[]?> ExtractAsync(string path, double from, double? seconds, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var args = new System.Collections.Generic.List<string> { "-nostdin", "-hide_banner", "-v", "error" };
        if (from > 0)
        {
            args.AddRange(["-ss", from.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-i", path]);
        if (seconds is { } length)
        {
            args.AddRange(["-t", length.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-vn", "-map", "0:a:0", "-ac", "1", "-ar", "11025", "-f", "chromaprint", "-fp_format", "raw", "-"]);
        var (code, output) = await RunAsync(args, timeout, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            _logger.LogDebug("Comseg: fingerprinting {Path} from {From} s failed (exit {Code})", path, from, code);
            return null;
        }

        return Fingerprint.FromBytes(output);
    }

    /// <summary>Runs ffmpeg; a run past its time limit is killed and reported as exit code -2, while the caller's cancellation still throws.</summary>
    private async Task<(int Code, byte[] Output)> RunAsync(System.Collections.Generic.IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = _ffmpeg(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        var tail = new OutputTail();
        process.ErrorDataReceived += (_, e) => tail.Add(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning("Comseg: ffmpeg could not be started for fingerprinting: {Problem}", ex.Message);
            return (-1, []);
        }

        process.BeginErrorReadLine();
        using var output = new MemoryStream();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ComskipRunner.TryKill(process);
            _logger.LogWarning("Comseg: ffmpeg took longer than {Timeout} fingerprinting and was stopped", timeout);
            return (-2, []);
        }
        catch (OperationCanceledException)
        {
            ComskipRunner.TryKill(process);
            throw;
        }

        return (process.ExitCode, output.ToArray());
    }
}
