using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>A finished recording the store keeps: its channel, when it aired, how long it is and where its breaks were.</summary>
/// <param name="Path">The recording file.</param>
/// <param name="Channel">The channel's name in Jellyfin.</param>
/// <param name="StartUtc">When it started, when known.</param>
/// <param name="AddedUtc">When the store took it.</param>
/// <param name="Seconds">Its length, from its fingerprint.</param>
/// <param name="Breaks">Its final breaks.</param>
/// <param name="Coverage">The share of its breaks with advert audio heard in the recordings stored before it, or null when there were none.</param>
public sealed record StoredRecordingInfo(string Path, string Channel, DateTime? StartUtc, DateTime AddedUtc, double Seconds, IReadOnlyList<Break> Breaks, double? Coverage = null);

/// <summary>A stored recording with its fingerprint, and an index of it built on first use.</summary>
public sealed class StoredRecording
{
    private FingerprintIndex? _index;

    /// <summary>Initializes a new instance of the <see cref="StoredRecording"/> class.</summary>
    /// <param name="info">What is known about it.</param>
    /// <param name="items">Its fingerprint.</param>
    public StoredRecording(StoredRecordingInfo info, uint[] items)
    {
        Info = info;
        Items = items;
    }

    /// <summary>Gets what is known about it.</summary>
    public StoredRecordingInfo Info { get; }

    /// <summary>Gets its fingerprint.</summary>
    public uint[] Items { get; }

    /// <summary>Gets its index.</summary>
    public FingerprintIndex Index => _index ??= new FingerprintIndex(Items);
}

/// <summary>
/// What the plugin has heard on each channel: the fingerprint of each
/// finished recording with its final breaks, under
/// &lt;data&gt;/comseg/audio/&lt;channel&gt;. A recording in progress is matched against
/// them: audio heard inside their breaks is advert audio, audio heard
/// outside them is programme audio, and audio heard at a break's edge says
/// where a break starts or ends. Only the newest recordings are kept, and
/// none older than 45 days, as campaigns and sponsors change.
/// </summary>
public sealed class AudioStore
{
    /// <summary>How many recordings a channel keeps.</summary>
    public const int Keep = 20;

    /// <summary>How old a kept recording may get.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(45);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly IApplicationPaths _paths;
    private readonly ILogger<AudioStore> _logger;

    /// <summary>Initializes a new instance of the <see cref="AudioStore"/> class.</summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public AudioStore(IApplicationPaths paths, ILogger<AudioStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>A channel's folder name: its name in lower case, without accents, other characters as dashes.</summary>
    /// <param name="channel">The channel's name.</param>
    /// <returns>The folder name.</returns>
    public static string Key(string channel)
    {
        var builder = new StringBuilder();
        foreach (var c in channel.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        var key = string.Join('-', builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return key.Length == 0 ? "channel" : key;
    }

    /// <summary>Keeps a finished recording's fingerprint and breaks, replacing an earlier copy of the same recording, and drops what is too many or too old.</summary>
    /// <param name="info">The recording.</param>
    /// <param name="items">Its fingerprint.</param>
    public void Add(StoredRecordingInfo info, uint[] items)
    {
        var dir = Folder(info.Channel);
        try
        {
            Directory.CreateDirectory(dir);
            var name = Workspace.Hash(info.Path);
            Fingerprint.Save(Path.Combine(dir, name + ".fp"), items);
            File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(info, Json));
            Prune(info.Channel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Comseg: could not keep the audio of {Path}: {Problem}", info.Path, ex.Message);
        }
    }

    /// <summary>A channel's kept recordings, newest first, without one of them.</summary>
    /// <param name="channel">The channel's name.</param>
    /// <param name="except">A recording to leave out (the one being matched), or null.</param>
    /// <returns>The recordings.</returns>
    public IReadOnlyList<StoredRecording> Load(string channel, string? except = null)
    {
        var list = new List<StoredRecording>();
        foreach (var (info, fp) in Entries(channel))
        {
            if (except is not null && string.Equals(info.Path, except, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                list.Add(new StoredRecording(info, Fingerprint.Load(fp)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Comseg: could not read the kept audio of {Path}: {Problem}", info.Path, ex.Message);
            }
        }

        return list;
    }

    /// <summary>How many recordings a channel keeps now.</summary>
    /// <param name="channel">The channel's name.</param>
    /// <returns>The count.</returns>
    public int Count(string channel) => Entries(channel).Count;

    private void Prune(string channel)
    {
        var entries = Entries(channel);
        var now = DateTime.UtcNow;
        for (var i = 0; i < entries.Count; i++)
        {
            var (info, fp) = entries[i];
            if (i >= Keep || now - (info.StartUtc ?? info.AddedUtc) > MaxAge)
            {
                File.Delete(fp);
                File.Delete(Path.ChangeExtension(fp, ".json"));
            }
        }
    }

    /// <summary>A channel's entries with readable metadata and a fingerprint file, newest first.</summary>
    private List<(StoredRecordingInfo Info, string Fp)> Entries(string channel)
    {
        var dir = Folder(channel);
        var entries = new List<(StoredRecordingInfo, string)>();
        if (!Directory.Exists(dir))
        {
            return entries;
        }

        foreach (var json in Directory.EnumerateFiles(dir, "*.json"))
        {
            var fp = Path.ChangeExtension(json, ".fp");
            try
            {
                if (File.Exists(fp) && JsonSerializer.Deserialize<StoredRecordingInfo>(File.ReadAllText(json), Json) is { } info)
                {
                    entries.Add((info, fp));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogDebug("Comseg: skipping kept audio {File}: {Problem}", json, ex.Message);
            }
        }

        return entries.OrderByDescending(e => e.Item1.StartUtc ?? e.Item1.AddedUtc).ToList();
    }

    private string Folder(string channel) => Path.Combine(_paths.DataPath, "comseg", "audio", Key(channel));
}
