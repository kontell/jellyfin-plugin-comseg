using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    private readonly Lock _writes = new();

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
    /// <returns>False when it could not be written.</returns>
    public bool Save(RecordingRecord record)
    {
        lock (_writes)
        {
            return Write(record, replace: true) == SaveResult.Saved;
        }
    }

    /// <summary>
    /// Writes a record unless the recording has one already, so a channel an
    /// admin set is kept. The check and the write are one step: the file is
    /// moved into place only where none is, under the same lock as every
    /// other write.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <returns>Whether it was written, was there already, or could not be written.</returns>
    public SaveResult SaveIfMissing(RecordingRecord record)
    {
        lock (_writes)
        {
            return Write(record, replace: false);
        }
    }

    /// <summary>Forgets a recording's record.</summary>
    /// <param name="mediaPath">The recording.</param>
    /// <returns>False when it could not be deleted.</returns>
    public bool Delete(string mediaPath)
    {
        lock (_writes)
        {
            try
            {
                File.Delete(FileFor(mediaPath));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Comseg: could not delete the record of {Path}: {Problem}", mediaPath, ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Deletes the records of recordings whose files are gone, but only where
    /// the recording root that holds the file is there: a drive that is not
    /// mounted yet when the server starts must not cost its recordings their
    /// channels, and a record under no known root is kept. A folder that
    /// cannot be read is left for a later sweep.
    /// </summary>
    /// <param name="roots">The recording roots.</param>
    /// <returns>How many were deleted.</returns>
    public int Sweep(IReadOnlyList<string> roots)
    {
        var deleted = 0;
        try
        {
            var dir = Folder();
            if (!Directory.Exists(dir))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<RecordingRecord>(File.ReadAllText(file), Json);
                    var root = record is null ? null : roots.FirstOrDefault(r => RecordingPaths.IsUnder(record.Path, [r]));
                    if (record is null || (root is not null && Directory.Exists(root) && !File.Exists(record.Path)))
                    {
                        lock (_writes)
                        {
                            File.Delete(file);
                        }

                        deleted++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Left for the next sweep.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Comseg: could not tidy the recording records: {Problem}", ex.Message);
        }

        return deleted;
    }

    private SaveResult Write(RecordingRecord record, bool replace)
    {
        var file = FileFor(record.Path);
        var temp = file + ".tmp";
        try
        {
            if (!replace && File.Exists(file))
            {
                return SaveResult.Existed;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
            File.Move(temp, file, overwrite: replace);
            return SaveResult.Saved;
        }
        catch (IOException) when (!replace && File.Exists(file))
        {
            File.Delete(temp);
            return SaveResult.Existed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Comseg: could not write the record of {Path}: {Problem}", record.Path, ex.Message);
            return SaveResult.Failed;
        }
    }

    private string Folder() => Path.Combine(_paths.DataPath, "comseg", "records");

    private string FileFor(string mediaPath) => Path.Combine(Folder(), Workspace.Hash(mediaPath) + ".json");
}

/// <summary>What became of a record write.</summary>
public enum SaveResult
{
    /// <summary>Written.</summary>
    Saved,

    /// <summary>The recording had a record already, which was kept.</summary>
    Existed,

    /// <summary>It could not be written; a later attempt may succeed.</summary>
    Failed,
}
