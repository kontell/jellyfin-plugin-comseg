using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Comseg.Configuration;

namespace Jellyfin.Plugin.Comseg.Comskip;

/// <summary>
/// Builds the ini Comskip is run with: the base ini (embedded default or the
/// configured file) with the plugin's run-mode keys applied. Comskip reads the
/// first occurrence of a key, so an override is applied by removing every
/// existing line for the key and appending the new value, never by appending
/// alone.
/// </summary>
public static class ComskipIni
{
    private const string ResourceName = "Jellyfin.Plugin.Comseg.Comskip.comskip.ini";

    /// <summary>Gets the embedded default ini.</summary>
    /// <returns>The ini text.</returns>
    public static string DefaultText()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("embedded comskip.ini missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The base ini text: the configured file when set and present, else the embedded default. A file that is there but cannot be read throws.</summary>
    /// <param name="configuredPath">The configured ini path, possibly empty.</param>
    /// <returns>The base ini text.</returns>
    public static string BaseText(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return File.ReadAllText(configuredPath);
        }

        return DefaultText();
    }

    /// <summary>
    /// Which ini runs start from, as a sentence for the Comskip check: a path
    /// the server cannot find falls back to the built-in ini without failing,
    /// so the check is where an admin learns it did.
    /// </summary>
    /// <param name="configuredPath">The configured ini path, possibly empty.</param>
    /// <returns>The sentence.</returns>
    public static string Describe(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return "Ini: the built-in one.";
        }

        if (!File.Exists(configuredPath))
        {
            return "Ini: the server finds no file at " + configuredPath + ", so runs use the built-in one.";
        }

        try
        {
            using var stream = File.OpenRead(configuredPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Ini: " + configuredPath + " cannot be read (" + ex.Message + "), so runs fail until it can.";
        }

        return "Ini: " + configuredPath + ".";
    }

    /// <summary>The keys for a run against a recording that is still being written.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>Key/value overrides.</returns>
    public static IReadOnlyDictionary<string, string> LiveOverrides(PluginConfiguration config)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["live_tv"] = "1",
            ["live_tv_retries"] = Clamp(config.LiveTvRetries, 1, 100000),
            ["live_master_interval"] = Clamp(config.LiveMasterIntervalSeconds, 10, 3600),
            ["output_edl"] = "1",
            ["output_default"] = "0",
            ["thread_count"] = Clamp(config.ThreadCount, 1, 64),
        };
    }

    /// <summary>The keys for a full pass over a finished recording.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>Key/value overrides.</returns>
    public static IReadOnlyDictionary<string, string> CompletedOverrides(PluginConfiguration config)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["live_tv"] = "0",
            ["output_edl"] = "1",
            ["output_default"] = "0",
            ["thread_count"] = Clamp(config.ThreadCount, 1, 64),
        };
    }

    /// <summary>Applies overrides to an ini text.</summary>
    /// <param name="baseText">The base ini.</param>
    /// <param name="overrides">Keys to force.</param>
    /// <returns>The composed ini.</returns>
    public static string Compose(string baseText, IReadOnlyDictionary<string, string> overrides)
    {
        var builder = new StringBuilder();
        foreach (var rawLine in baseText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var key = KeyOf(line);
            if (key is not null && overrides.ContainsKey(key))
            {
                continue;
            }

            builder.Append(line).Append('\n');
        }

        builder.Append("\n; comseg run settings\n");
        foreach (var pair in overrides.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The key of an ini line, or null for comments and blank lines.</summary>
    /// <param name="line">One line.</param>
    /// <returns>The key.</returns>
    internal static string? KeyOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#' || trimmed[0] == '[')
        {
            return null;
        }

        var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return null;
        }

        return trimmed.Substring(0, eq).Trim();
    }

    private static string Clamp(int value, int min, int max)
        => Math.Clamp(value, min, max).ToString(CultureInfo.InvariantCulture);
}
