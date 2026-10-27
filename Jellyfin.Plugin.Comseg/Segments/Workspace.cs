using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Comseg.Configuration;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Comseg.Segments;

/// <summary>Where Comskip's output goes: a folder per job under the work directory.</summary>
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
}
