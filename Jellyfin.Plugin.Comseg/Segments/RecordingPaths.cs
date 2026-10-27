using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Comseg.Configuration;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Comseg.Segments;

/// <summary>
/// Which files count as recordings: anything under Jellyfin's recording
/// folders (the default one and the movie/series paths from the Live TV
/// settings) or under a folder the plugin configuration adds.
///
/// The recordings manager is resolved per call, not injected: it depends on
/// IProviderManager, which depends on IMediaSegmentManager, which constructs
/// every IMediaSegmentProvider, and the provider uses this class. Taking it
/// in the constructor is a cycle that stops the server from starting.
/// </summary>
public sealed class RecordingPaths
{
    private readonly IServiceProvider _services;

    /// <summary>Initializes a new instance of the <see cref="RecordingPaths"/> class.</summary>
    /// <param name="services">The service provider.</param>
    public RecordingPaths(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Splits the configured extra paths (one per line, or ;-separated).</summary>
    /// <param name="text">The setting.</param>
    /// <returns>The paths.</returns>
    public static IReadOnlyList<string> Split(string text)
        => text.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    /// <summary>Whether a file lies under one of the roots (a root itself does not count).</summary>
    /// <param name="path">The file.</param>
    /// <param name="roots">The folders.</param>
    /// <returns>True when under a root.</returns>
    public static bool IsUnder(string path, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (fullRoot.Length == 0 || full.Equals(fullRoot, comparison))
            {
                continue;
            }

            if (full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The recording folders: Jellyfin's plus the configured extras.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>Folder paths.</returns>
    public IReadOnlyList<string> Roots(PluginConfiguration config)
    {
        var roots = new List<string>();
        foreach (var folder in Recordings.GetRecordingFolders())
        {
            if (folder.Locations is not null)
            {
                roots.AddRange(folder.Locations);
            }
        }

        roots.AddRange(Split(config.ExtraRecordingPaths));
        return roots;
    }

    /// <summary>Whether a file is a recording the plugin should handle.</summary>
    /// <param name="path">The file.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>True for a recording.</returns>
    public bool IsRecording(string path, PluginConfiguration config) => IsUnder(path, Roots(config));

    /// <summary>Whether a file is still being written by the DVR.</summary>
    /// <param name="path">The file.</param>
    /// <returns>True while recording.</returns>
    public bool IsActiveRecording(string path) => Recordings.GetActiveRecordingInfo(path) is not null;

    private IRecordingsManager Recordings => _services.GetRequiredService<IRecordingsManager>();
}
