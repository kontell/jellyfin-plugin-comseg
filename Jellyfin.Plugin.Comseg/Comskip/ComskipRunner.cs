using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Comskip;

/// <summary>
/// Launches the Comskip binary. Always with an explicit --output work
/// directory (Comskip's default is beside the recording, which is read-only
/// to the server more often than not), with ArgumentList so any file name
/// survives, and treating "a .edl appeared" as success: Comskip exits 1 when
/// it found no commercials, which is a result, not a failure.
/// </summary>
public sealed class ComskipRunner
{
    private readonly ILogger<ComskipRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="ComskipRunner"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public ComskipRunner(ILogger<ComskipRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>The output base name Comskip derives from an input path.</summary>
    /// <param name="mediaPath">The recording.</param>
    /// <returns>The file name without extension.</returns>
    public static string BaseName(string mediaPath) => Path.GetFileNameWithoutExtension(mediaPath);

    /// <summary>Runs a full pass to completion.</summary>
    /// <param name="comskipPath">The binary.</param>
    /// <param name="iniPath">The composed ini.</param>
    /// <param name="workDir">Where the outputs go.</param>
    /// <param name="mediaPath">The recording.</param>
    /// <param name="timeout">How long before the process is killed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The run result.</returns>
    public async Task<RunResult> RunAsync(
        string comskipPath,
        string iniPath,
        string workDir,
        string mediaPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDir);
        var tail = new OutputTail();
        using var process = new Process { StartInfo = StartInfo(comskipPath, iniPath, workDir, mediaPath), EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => tail.Add(e.Data);
        process.ErrorDataReceived += (_, e) => tail.Add(e.Data);
        var started = DateTime.UtcNow;
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Comskip timed out after {Timeout} on {Media}", timeout, mediaPath);
            TryKill(process);
            return new RunResult(false, null, tail.ToString(), DateTime.UtcNow - started);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Comskip could not be started from {Path}", comskipPath);
            return new RunResult(false, null, ex.Message, DateTime.UtcNow - started);
        }

        var edl = Path.Combine(workDir, BaseName(mediaPath) + ".edl");
        if (!File.Exists(edl))
        {
            _logger.LogWarning("Comskip exited {Code} without writing {Edl}: {Tail}", process.ExitCode, edl, tail);
            return new RunResult(false, null, tail.ToString(), DateTime.UtcNow - started);
        }

        return new RunResult(true, edl, tail.ToString(), DateTime.UtcNow - started);
    }

    /// <summary>Starts Comskip in live mode against a recording still being written, and returns without waiting.</summary>
    /// <param name="comskipPath">The binary.</param>
    /// <param name="iniPath">The composed live ini.</param>
    /// <param name="workDir">Where the outputs go.</param>
    /// <param name="mediaPath">The recording.</param>
    /// <returns>The running process, or null when it could not be started.</returns>
    public LiveProcess? StartLive(string comskipPath, string iniPath, string workDir, string mediaPath)
    {
        Directory.CreateDirectory(workDir);
        var tail = new OutputTail();
        var process = new Process { StartInfo = StartInfo(comskipPath, iniPath, workDir, mediaPath), EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => tail.Add(e.Data);
        process.ErrorDataReceived += (_, e) => tail.Add(e.Data);
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Comskip could not be started from {Path}", comskipPath);
            process.Dispose();
            return null;
        }

        var baseName = BaseName(mediaPath);
        return new LiveProcess(
            process,
            tail,
            mediaPath,
            Path.Combine(workDir, baseName + ".master.edl"),
            Path.Combine(workDir, baseName + ".edl"));
    }

    /// <summary>Asks the binary for its banner, to check a configured path.</summary>
    /// <param name="comskipPath">The binary.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The first banner line, or the error.</returns>
    public async Task<(bool Ok, string Text)> ProbeAsync(string comskipPath, CancellationToken cancellationToken)
    {
        var tail = new OutputTail();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = comskipPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--help");
        process.OutputDataReceived += (_, e) => tail.Add(e.Data);
        process.ErrorDataReceived += (_, e) => tail.Add(e.Data);
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            TryKill(process);
            return (false, ex.Message);
        }

        foreach (var line in tail.ToString().Split('\n'))
        {
            if (line.Contains("Comskip", StringComparison.OrdinalIgnoreCase))
            {
                return (true, line.Trim());
            }
        }

        return (false, tail.ToString().Trim());
    }

    private static ProcessStartInfo StartInfo(string comskipPath, string iniPath, string workDir, string mediaPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = comskipPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };
        info.ArgumentList.Add("--ini=" + iniPath);
        info.ArgumentList.Add("--output=" + workDir);
        info.ArgumentList.Add(mediaPath);
        return info;
    }

    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // The patched live mode forks analysis children: kill the tree.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>The outcome of a full pass.</summary>
    /// <param name="Ran">Whether a .edl was produced.</param>
    /// <param name="EdlPath">The .edl, when produced.</param>
    /// <param name="Tail">The last part of Comskip's output.</param>
    /// <param name="Elapsed">Wall time.</param>
    public sealed record RunResult(bool Ran, string? EdlPath, string Tail, TimeSpan Elapsed);

    /// <summary>A running live-mode Comskip.</summary>
    public sealed class LiveProcess : IDisposable
    {
        private readonly Process _process;
        private readonly OutputTail _tail;

        internal LiveProcess(Process process, OutputTail tail, string mediaPath, string masterEdlPath, string edlPath)
        {
            _process = process;
            _tail = tail;
            MediaPath = mediaPath;
            MasterEdlPath = masterEdlPath;
            EdlPath = edlPath;
        }

        /// <summary>Gets the file this Comskip reads: the recording, or a scaled copy of it.</summary>
        public string MediaPath { get; }

        /// <summary>Gets the periodic full-analysis output (the patched binary's &lt;base&gt;.master.edl).</summary>
        public string MasterEdlPath { get; }

        /// <summary>Gets Comskip's own .edl, written by its final pass when the input ends.</summary>
        public string EdlPath { get; }

        /// <summary>Gets a value indicating whether the process has exited.</summary>
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

        /// <summary>Gets the process exit code, or null while running.</summary>
        public int? ExitCode => HasExited ? _process.ExitCode : null;

        /// <summary>Gets the last part of the process output.</summary>
        public string Tail => _tail.ToString();

        /// <summary>Kills the process and its analysis children.</summary>
        public void Kill() => TryKill(_process);

        /// <inheritdoc />
        public void Dispose()
        {
            TryKill(_process);
            _process.Dispose();
        }
    }
}
