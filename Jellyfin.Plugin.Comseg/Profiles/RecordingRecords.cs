using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Profiles;

/// <summary>
/// The recording records, one small JSON file per recording under
/// &lt;data&gt;/comseg/records, named by a hash of the recording's path. They
/// live under the data path rather than the work directory so that they
/// outlive a restart and a work directory on tmpfs.
/// </summary>
public sealed class RecordingRecords
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly IApplicationPaths _paths;
    private readonly ILogger<RecordingRecords> _logger;

    /// <summary>Initializes a new instance of the <see cref="RecordingRecords"/> class.</summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public RecordingRecords(IApplicationPaths paths, ILogger<RecordingRecords> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>The record for a recording, or null when there is none or it cannot be read.</summary>
    /// <param name="mediaPath">The recording.</param>
    /// <returns>The record.</returns>
    public RecordingRecord? Get(string mediaPath)
    {
        var file = FileFor(mediaPath);
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<RecordingRecord>(File.ReadAllText(file), Json);

            // A hash collision, however unlikely, must not lend one recording another's channel.
            return record is not null && string.Equals(record.Path, mediaPath, StringComparison.Ordinal) ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Comseg: could not read the record of {Path}: {Problem}", mediaPath, ex.Message);
            return null;
        }
    }

    /// <summary>Writes a recording's record, replacing any earlier one.</summary>
    /// <param name="record">The record.</param>
    public void Save(RecordingRecord record)
    {
        var file = FileFor(record.Path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Comseg: could not write the record of {Path}: {Problem}", record.Path, ex.Message);
        }
    }

    /// <summary>Writes a record unless the recording has one already, so a channel an admin set is kept.</summary>
    /// <param name="record">The record.</param>
    /// <returns>True when it was written.</returns>
    public bool SaveIfMissing(RecordingRecord record)
    {
        if (Get(record.Path) is not null)
        {
            return false;
        }

        Save(record);
        return true;
    }

    /// <summary>Forgets a recording's record.</summary>
    /// <param name="mediaPath">The recording.</param>
    public void Delete(string mediaPath)
    {
        try
        {
            File.Delete(FileFor(mediaPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Comseg: could not delete the record of {Path}: {Problem}", mediaPath, ex.Message);
        }
    }

    /// <summary>
    /// Deletes the records of recordings whose files are gone. Only where the
    /// folder two levels above the file is still there, the recordings folder
    /// in Jellyfin's layout: a drive that is not mounted yet when the server
    /// starts must not cost every recording its channel.
    /// </summary>
    /// <returns>How many were deleted.</returns>
    public int Sweep()
    {
        var dir = Folder();
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<RecordingRecord>(File.ReadAllText(file), Json);
                if (record is null || (!File.Exists(record.Path) && Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(record.Path)))))
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Left for the next sweep.
            }
        }

        return deleted;
    }

    private string Folder() => Path.Combine(_paths.DataPath, "comseg", "records");

    private string FileFor(string mediaPath) => Path.Combine(Folder(), Workspace.Hash(mediaPath) + ".json");
}
