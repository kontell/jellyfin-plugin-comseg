using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Profiles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>
/// Keeps a finished recording's audio for its channel once its full pass has
/// found its breaks: the fingerprint of the whole file, a few seconds an hour,
/// and how many of its breaks carried audio heard on the channel before.
/// Runs in the background, one recording at a time, so a pass never waits
/// for it and a scan over many recordings does not fingerprint them all at once.
/// </summary>
public sealed class AudioLearner
{
    private readonly Fingerprinter _fingerprinter;
    private readonly AudioStore _store;
    private readonly ILogger<AudioLearner> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="AudioLearner"/> class.</summary>
    /// <param name="fingerprinter">The fingerprinter.</param>
    /// <param name="store">The audio store.</param>
    /// <param name="logger">The logger.</param>
    public AudioLearner(Fingerprinter fingerprinter, AudioStore store, ILogger<AudioLearner> logger)
    {
        _fingerprinter = fingerprinter;
        _store = store;
        _logger = logger;
    }

    /// <summary>Starts keeping a recording's audio, without waiting for it.</summary>
    /// <param name="mediaPath">The recording.</param>
    /// <param name="settings">The settings it ran with, which name its channel.</param>
    /// <param name="breaks">Its final breaks.</param>
    public void Learn(string mediaPath, RunSettings settings, IReadOnlyList<Break> breaks)
    {
        if (!settings.AudioEvidence || settings.Record is not { } record || breaks.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await _oneAtATime.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!await _fingerprinter.AvailableAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    return;
                }

                var items = await _fingerprinter.ExtractAsync(mediaPath, 0, null, CancellationToken.None).ConfigureAwait(false);
                if (items is null || items.Length == 0)
                {
                    _logger.LogWarning("Comseg: could not fingerprint {Media}; its audio is not kept", mediaPath);
                    return;
                }

                var others = _store.Load(record.ChannelName, except: mediaPath);
                var coverage = AudioTrust.Coverage(items, breaks, others);
                _store.Add(new StoredRecordingInfo(mediaPath, record.ChannelName, record.StartUtc, DateTime.UtcNow, Fingerprint.TimeOf(items.Length), breaks, coverage), items);
                var kept = Math.Min(others.Count + 1, AudioStore.Keep);
                if (coverage is { } share)
                {
                    _logger.LogInformation("Comseg: kept the audio of {Media} for {Channel} ({Count} recordings kept); {Share:P0} of its breaks carried audio heard on the channel before", mediaPath, record.ChannelName, kept, share);
                }
                else
                {
                    _logger.LogInformation("Comseg: kept the audio of {Media} for {Channel} ({Count} recordings kept)", mediaPath, record.ChannelName, kept);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Comseg: keeping the audio of {Media} failed", mediaPath);
            }
            finally
            {
                _oneAtATime.Release();
            }
        });
    }
}
