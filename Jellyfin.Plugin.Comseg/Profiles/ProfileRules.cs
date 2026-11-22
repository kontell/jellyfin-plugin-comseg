using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Configuration;

namespace Jellyfin.Plugin.Comseg.Profiles;

/// <summary>Which profile a recording runs with, and what that changes.</summary>
public static class ProfileRules
{
    private static readonly Regex Key = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// The keys the plugin sets for its runs itself. A profile cannot change
    /// them: the run-mode keys are applied last and win.
    /// </summary>
    public static readonly IReadOnlySet<string> PluginKeys = new HashSet<string>(
        ComskipIni.LiveOverrides(new PluginConfiguration()).Keys
            .Concat(ComskipIni.CompletedOverrides(new PluginConfiguration()).Keys)
            .Concat(ComskipIni.KeepFramesKeys.Keys),
        StringComparer.Ordinal);

    /// <summary>The settings a recording runs with.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="record">The recording's record, when there is one.</param>
    /// <returns>The settings.</returns>
    public static RunSettings Resolve(PluginConfiguration config, RecordingRecord? record)
    {
        var profile = record is null ? null : Match(config.Profiles ?? [], record.ChannelName, record.ChannelGroups);
        if (profile is null)
        {
            return new RunSettings(config, null, record, new Dictionary<string, string>(StringComparer.Ordinal), []);
        }

        var (keys, problems) = ParseKeys(profile.IniKeys);
        var effective = config;
        if (profile.AnalysisHeight >= 0)
        {
            effective = config.Copy();
            effective.AnalysisHeight = profile.AnalysisHeight;
        }

        return new RunSettings(effective, profile, record, keys, problems);
    }

    /// <summary>The first profile that lists the channel, or one of its playlist groups.</summary>
    /// <param name="profiles">The profiles, in order.</param>
    /// <param name="channelName">The channel's name in Jellyfin.</param>
    /// <param name="channelGroups">The channel's groups in the tuner's playlist, when known.</param>
    /// <returns>The profile, or null.</returns>
    public static ChannelProfile? Match(IEnumerable<ChannelProfile> profiles, string? channelName, IReadOnlyList<string>? channelGroups = null)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return null;
        }

        var groups = channelGroups ?? [];
        return profiles.FirstOrDefault(p => p is not null
            && (Patterns(p.Channels).Any(pattern => Matches(pattern, channelName))
                || Patterns(p.Groups).Any(pattern => groups.Any(g => Matches(pattern, g)))));
    }

    /// <summary>
    /// Whether a channel or group name fits a pattern: case and accents are
    /// ignored, so "RTÉ One" fits a channel the playlist calls "RTE One", and *
    /// stands for any text.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="channelName">The channel's or group's name.</param>
    /// <returns>True when it fits.</returns>
    public static bool Matches(string pattern, string channelName)
    {
        var regex = "^" + string.Join(".*", Fold(pattern).Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(Fold(channelName), regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>The channel or group patterns of a profile: separated by semicolons or lines, blanks dropped.</summary>
    /// <param name="channels">The profile's channel or group list.</param>
    /// <returns>The patterns.</returns>
    public static IReadOnlyList<string> Patterns(string? channels)
        => (channels ?? string.Empty).Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Reads a profile's ini keys: key=value pairs separated by semicolons or
    /// lines; a line starting with # is a comment. A key the plugin sets itself,
    /// or a pair it cannot read, is left out and reported.
    /// </summary>
    /// <param name="text">The profile's keys.</param>
    /// <returns>The keys, and what was left out and why.</returns>
    public static (IReadOnlyDictionary<string, string> Keys, IReadOnlyList<string> Problems) ParseKeys(string? text)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var part in (text ?? string.Empty).Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith('#'))
            {
                continue;
            }

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = eq > 0 ? part[..eq].Trim() : string.Empty;
            if (eq <= 0 || !Key.IsMatch(key))
            {
                problems.Add("\"" + part + "\" is not a key=value pair");
                continue;
            }

            if (PluginKeys.Contains(key))
            {
                problems.Add(key + " is set by the plugin for every run; the profile's value is ignored");
                continue;
            }

            keys[key] = part[(eq + 1)..].Trim();
        }

        return (keys, problems);
    }

    /// <summary>What is wrong with the profiles, one line each, for the Comskip check.</summary>
    /// <param name="profiles">The profiles.</param>
    /// <returns>The problems; empty when there are none.</returns>
    public static IReadOnlyList<string> Check(IReadOnlyList<ChannelProfile> profiles)
    {
        var problems = new List<string>();
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var name = DisplayName(profile, i);
            if (Patterns(profile.Channels).Count == 0 && Patterns(profile.Groups).Count == 0)
            {
                problems.Add(name + ": no channels or playlist groups, so it never applies");
            }

            problems.AddRange(ParseKeys(profile.IniKeys).Problems.Select(p => name + ": " + p));
        }

        return problems;
    }

    /// <summary>A profile's name, or a stand-in for one left blank.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="index">Its place in the list, from 0.</param>
    /// <returns>The name.</returns>
    public static string DisplayName(ChannelProfile profile, int index)
        => string.IsNullOrWhiteSpace(profile.Name) ? "profile " + (index + 1).ToString(CultureInfo.InvariantCulture) : profile.Name.Trim();

    /// <summary>Lower case without accents, for comparing names.</summary>
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
