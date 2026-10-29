using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Live;

/// <summary>
/// The scaled copy a live job reads, from the decision to make one to the
/// file handed to the final pass. While the recording runs it says which
/// file the live Comskip opens: the copy once it has its first frames, the
/// recording when no copy is wanted or the recording is neither taller than
/// the height asked nor HDR, and the recording for good once the copy has
/// failed. When
/// the recording ends it waits for the copy to catch up, and hands it on
/// only if it covers the whole recording.
/// </summary>
public sealed class LiveCopy : IDisposable
{
    private static readonly TimeSpan StartLimit = TimeSpan.FromMinutes(2);

    private readonly ScaledCopy _copies;
    private readonly ILogger _logger;
    private readonly string _recording;
    private readonly string _path;
    private readonly int _height;
    private ScaledCopy.Following? _ffmpeg;
    private DateTime _startedUtc;
    private int _probes;
    private bool _hdr;
    private volatile State _state;

    /// <summary>Initializes a new instance of the <see cref="LiveCopy"/> class.</summary>
    /// <param name="copies">The scaled copies.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="recording">The recording.</param>
    /// <param name="path">Where the copy goes.</param>
    /// <param name="height">The height to scale to, or 0 for no copy.</param>
    public LiveCopy(ScaledCopy copies, ILogger logger, string recording, string path, int height)
    {
        _copies = copies;
        _logger = logger;
        _recording = recording;
        _path = path;
        _height = height;
        _state = height == 0 ? State.Direct : State.Undecided;
    }

    private enum State
    {
        Undecided,
        Starting,
        Reading,
        Direct,
    }

    /// <summary>Gets the copy's height while the live Comskip reads it, else null.</summary>
    public int? Height => _state == State.Reading ? _height : null;

    /// <summary>Gets why the job reads the recording after all, when a copy failed.</summary>
    public string? Problem { get; private set; }

    /// <summary>The file the live Comskip opens now.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The copy or the recording, or null while the copy is starting and has no frames yet.</returns>
    public async Task<string?> InputAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        switch (_state)
        {
            case State.Undecided:
                var source = await _copies.ProbeAsync(_recording, false, cancellationToken).ConfigureAwait(false);
                if (source is null && ++_probes < 4)
                {
                    return null; // the recording's first picture may not be readable yet
                }

                if (source is null || (source.Height <= _height && !source.Hdr))
                {
                    if (source is null)
                    {
                        _logger.LogWarning("Comseg: no picture size for {Path}; analysing the recording itself", _recording);
                    }

                    _state = State.Direct;
                    return _recording;
                }

                _hdr = source.Hdr;
                _ffmpeg = _copies.StartFollowing(_recording, _path, _height, _hdr, config);
                if (_ffmpeg is null)
                {
                    return Fail("the scaled copy could not be started");
                }

                _startedUtc = DateTime.UtcNow;
                _state = State.Starting;
                return null;

            case State.Starting:
                if (_ffmpeg!.HasExited)
                {
                    return Fail("the scaled copy stopped before writing anything");
                }

                if (RecordingWatcher.FileReady(_path))
                {
                    _state = State.Reading;
                    if (_hdr)
                    {
                        _logger.LogInformation("Comseg: analysing a {Height}p copy of {Path} made on the GPU, tone mapped from HDR", _height, _recording);
                    }
                    else
                    {
                        _logger.LogInformation("Comseg: analysing a {Height}p copy of {Path} made on the GPU", _height, _recording);
                    }

                    return _path;
                }

                return DateTime.UtcNow - _startedUtc < StartLimit ? null : Fail("the scaled copy wrote nothing in two minutes");

            case State.Reading:
                // A copy that stopped while the recording runs leaves Comskip waiting
                // for frames that never come.
                return _ffmpeg!.HasExited ? Fail("the scaled copy stopped") : _path;

            default:
                return _recording;
        }
    }

    /// <summary>Once the recording has ended: the copy for the final pass, when the job read one and it covers the whole recording.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The copy, or null.</returns>
    public async Task<string?> FinishAsync(CancellationToken cancellationToken)
    {
        if (_state != State.Reading || _ffmpeg is null)
        {
            return null;
        }

        await _ffmpeg.FinishAsync(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        var recording = await _copies.ProbeAsync(_recording, true, cancellationToken).ConfigureAwait(false);
        var copy = await _copies.ProbeAsync(_path, true, cancellationToken).ConfigureAwait(false);
        if (!ScaledCopy.Covers(recording, copy))
        {
            _logger.LogWarning("Comseg: the scaled copy of {Path} does not cover the recording; the final pass starts afresh", _recording);
            return null;
        }

        return _path;
    }

    /// <inheritdoc />
    public void Dispose() => _ffmpeg?.Dispose();

    private string Fail(string problem)
    {
        _logger.LogWarning("Comseg: {Problem} for {Path}; analysing the recording itself. {Tail}", problem, _recording, _ffmpeg?.Tail);
        Problem = problem + "; analysing the recording itself";
        _ffmpeg?.Dispose();
        _ffmpeg = null;
        _state = State.Direct;
        return _recording;
    }
}
