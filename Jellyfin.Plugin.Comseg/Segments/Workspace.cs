using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Comseg.Configuration;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Comseg.Segments;

/// <summary>
/// Where Comskip's output goes: a folder per job under the work directory.
/// Also keeps the provisional marks. An item whose rows came from live
/// analysis alone carries one until a full pass has replaced them, so the
/// scheduled scan still runs that pass although the item has rows: without
/// it, a final pass that never stored (a restart in the middle of it, a
/// timeout, a pass that wrote nothing) would leave the live result for good.
/// A mark is a file under the data path rather than the work directory, so
/// it outlives a restart and a work directory on tmpfs.
/// </summary>
public sealed class Workspace
{
    private readonly IApplicationPaths _paths;

    /// <summary>Initializes a new instance of the <see cref="Workspace"/> class.</summary>
    /// <param name="paths">The application paths.</param>
    public Workspace(IApplicationPaths paths)
    {
        _paths = paths;
    }

    /// <summary>The work root: the configured directory, or &lt;data&gt;/comseg.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The root path.</returns>
    public string Root(PluginConfiguration config)
        => string.IsNullOrWhiteSpace(config.WorkDirectory)
            ? Path.Combine(_paths.DataPath, "comseg")
            : config.WorkDirectory.Trim();

    /// <summary>A job folder for a recording path, stable across restarts.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="kind">"live" or "scan".</param>
    /// <param name="mediaPath">The recording.</param>
    /// <returns>The folder path.</returns>
    public string JobDir(PluginConfiguration config, string kind, string mediaPath)
        => Path.Combine(Root(config), kind, Hash(mediaPath));

    /// <summary>Marks an item's rows as live analysis only.</summary>
    /// <param name="itemId">The item.</param>
    public void MarkProvisional(Guid itemId)
    {
        var path = ProvisionalPath(itemId);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Array.Empty<byte>());
        }
    }

    /// <summary>Drops an item's provisional mark, ignoring failures.</summary>
    /// <param name="itemId">The item.</param>
    public void ClearProvisional(Guid itemId)
    {
        try
        {
            File.Delete(ProvisionalPath(itemId));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether an item's rows are live analysis only.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True when marked.</returns>
    public bool IsProvisional(Guid itemId) => File.Exists(ProvisionalPath(itemId));

    /// <summary>A short stable hash of a path.</summary>
    /// <param name="text">The path.</param>
    /// <returns>16 hex characters.</returns>
    public static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>Deletes a job folder, ignoring failures.</summary>
    /// <param name="dir">The folder.</param>
    public static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string ProvisionalPath(Guid itemId)
        => Path.Combine(_paths.DataPath, "comseg", "provisional", itemId.ToString("N", CultureInfo.InvariantCulture));
}
