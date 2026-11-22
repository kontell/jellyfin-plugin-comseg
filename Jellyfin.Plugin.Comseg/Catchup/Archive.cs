using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Comseg.Catchup;

/// <summary>One channel of an M3U playlist, read the way Jellyfin's M3U tuner reads it.</summary>
/// <param name="Name">The channel name.</param>
/// <param name="Number">The channel number, when the entry carries one.</param>
/// <param name="ArchiveDays">Days of archive from catchup-days, timeshift or tvg-rec, when the entry says.</param>
/// <param name="Url">The live URL. It carries the account's token: never log it.</param>
/// <param name="GroupTitle">The entry's group-title, when it has one.</param>
public sealed record PlaylistEntry(string Name, string? Number, int? ArchiveDays, string Url, string? GroupTitle = null)
{
    /// <summary>
    /// Gets the groups the channel is in: the group-title split at semicolons,
    /// as Kodi's IPTV Simple client reads it, so "Sport;Sky Sports" is in
    /// "Sport" and in "Sky Sports".
    /// </summary>
    public IReadOnlyList<string> Groups => (GroupTitle ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>What a check of an archive playlist found.</summary>
/// <param name="Problems">Why the playlist cannot be downloaded; empty when it can.</param>
/// <param name="TotalSeconds">The summed segment durations.</param>
/// <param name="FirstSegmentUtc">When the first segment starts, from #EXT-X-PROGRAM-DATE-TIME.</param>
public sealed record PlaylistCheck(IReadOnlyList<string> Problems, double TotalSeconds, DateTime? FirstSegmentUtc);

/// <summary>
/// The catchup archive of a Flussonic-style IPTV server: finding a channel's
/// live URL in the tuner's playlist, turning it into the URL of a bounded
/// archive window, and checking the playlist that comes back.
///
/// Only <c>archive-{start}-{seconds}.m3u8</c> stops where it was asked to: the
/// Shift form pvr.iptvsimple builds (<c>?utc=&amp;lutc=</c>) and
/// <c>timeshift_abs</c> return three hours, and <c>timeshift_rel</c> runs on to
/// live. The archive holds the segments that start inside the window, so the
/// first one starts up to a target duration late; callers ask for a little
/// earlier. Every failure seen comes back as HTTP 200 (a stub video for a
/// wrong token or a time outside the archive, the live channel for a start in
/// the future, a window cut at now), which is why the playlist is checked.
/// </summary>
public static class Archive
{
    private static readonly Regex Attribute = new("([A-Za-z0-9_-]+)=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex LiveUrl = new(@"^(https?://[^/]+/.+/)[^/?]+\.m3u8(\?.*)?$", RegexOptions.Compiled);

    /// <summary>Parses an M3U playlist into its channels.</summary>
    /// <param name="text">The playlist.</param>
    /// <returns>The channels, in order.</returns>
    public static IReadOnlyList<PlaylistEntry> ParsePlaylist(string text)
    {
        var entries = new List<PlaylistEntry>();
        string? extInf = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                extInf = line;
                continue;
            }

            if (line.StartsWith('#') || extInf is null)
            {
                continue;
            }

            var attributes = Attributes(extInf);
            var name = ChannelName(extInf, attributes);
            if (name is not null)
            {
                entries.Add(new PlaylistEntry(name, ChannelNumber(extInf, attributes), ArchiveDays(attributes), line, attributes.GetValueOrDefault("group-title")));
            }

            extInf = null;
        }

        return entries;
    }

    /// <summary>
    /// The playlist entry for a channel. The name decides; the number only
    /// breaks a tie, or stands in when no name matches (a client that shows
    /// its own channel names).
    /// </summary>
    /// <param name="entries">The playlist.</param>
    /// <param name="name">The channel name the client shows.</param>
    /// <param name="number">The channel number the client shows.</param>
    /// <returns>The entry, or null when none or several match.</returns>
    public static PlaylistEntry? Find(IReadOnlyList<PlaylistEntry> entries, string? name, string? number)
    {
        var byName = string.IsNullOrWhiteSpace(name)
            ? new List<PlaylistEntry>()
            : entries.Where(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1)
        {
            return byName[0];
        }

        var pool = byName.Count > 1 ? byName : entries;
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        var byNumber = pool.Where(e => SameNumber(e.Number, number)).ToList();
        return byNumber.Count == 1 ? byNumber[0] : null;
    }

    /// <summary>The archive window of a Flussonic-style live URL (<c>.../NAME.m3u8</c>), or null for any other shape.</summary>
    /// <param name="liveUrl">The channel's live URL.</param>
    /// <param name="startUnix">The window's start, Unix seconds.</param>
    /// <param name="seconds">The window's length.</param>
    /// <returns>The archive URL, or null.</returns>
    public static string? ArchiveUrl(string liveUrl, long startUnix, int seconds)
    {
        var m = LiveUrl.Match(liveUrl);
        if (!m.Success || seconds <= 0)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{m.Groups[1].Value}archive-{startUnix}-{seconds}.m3u8{m.Groups[2].Value}");
    }

    /// <summary>Checks an archive playlist before anything is downloaded.</summary>
    /// <param name="requestedUrl">The URL asked for.</param>
    /// <param name="finalUrl">The URL that answered, after redirects.</param>
    /// <param name="playlist">The playlist text.</param>
    /// <param name="requestedStartUtc">The start asked for (the wanted start less the padding).</param>
    /// <param name="wantedStartUtc">The start the caller wants covered.</param>
    /// <param name="wantedSeconds">The length the caller wants covered, from the wanted start.</param>
    /// <param name="slackSeconds">How far after the requested start the first segment may begin.</param>
    /// <returns>The check.</returns>
    public static PlaylistCheck Check(string requestedUrl, string finalUrl, string playlist, DateTime requestedStartUtc, DateTime wantedStartUtc, double wantedSeconds, double slackSeconds)
    {
        var problems = new List<string>();
        var lines = playlist.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !lines[0].Trim().StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            problems.Add("the archive's answer is not a playlist");
            return new PlaylistCheck(problems, 0, null);
        }

        if (!string.Equals(Directory(requestedUrl), Directory(finalUrl), StringComparison.Ordinal))
        {
            problems.Add("the archive redirected away from the channel: a wrong token, or a time outside the archive");
        }

        if (!lines.Any(l => l.Trim() == "#EXT-X-ENDLIST"))
        {
            problems.Add("the playlist has no end (#EXT-X-ENDLIST): the archive is treating the start as live");
        }

        double total = 0;
        foreach (var line in lines.Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)))
        {
            var value = line.AsSpan(8);
            var comma = value.IndexOf(',');
            if (comma >= 0)
            {
                value = value[..comma];
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                total += d;
            }
        }

        DateTime? first = null;
        var pdt = lines.FirstOrDefault(l => l.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal));
        if (pdt is not null
            && DateTime.TryParse(pdt.AsSpan(25).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            first = parsed;
        }

        if (first is null)
        {
            problems.Add("the playlist does not say when it starts (#EXT-X-PROGRAM-DATE-TIME)");
            return new PlaylistCheck(problems, total, null);
        }

        // The archive begins with the first segment that starts inside the
        // window, so up to a target duration after the requested start.
        if (first.Value < requestedStartUtc.AddSeconds(-1) || first.Value > requestedStartUtc.AddSeconds(slackSeconds))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the archive starts at {first.Value:HH:mm:ss}, not at {requestedStartUtc:HH:mm:ss}"));
        }

        var wantedEnd = wantedStartUtc.AddSeconds(wantedSeconds);
        var archiveEnd = first.Value.AddSeconds(total);
        if (archiveEnd < wantedEnd.AddSeconds(-1))
        {
            var have = Math.Max(0, (archiveEnd - wantedStartUtc).TotalSeconds);
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the archive has {have:0} s of the {wantedSeconds:0} s asked for"));
        }

        return new PlaylistCheck(problems, total, first);
    }

    private static string Directory(string url)
    {
        var noQuery = url.Split('?', 2)[0];
        var slash = noQuery.LastIndexOf('/');
        return slash < 0 ? noQuery : noQuery[..slash];
    }

    private static Dictionary<string, string> Attributes(string extInf)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attribute.Matches(extInf))
        {
            map[m.Groups[1].Value] = m.Groups[2].Value;
        }

        return map;
    }

    /// <summary>
    /// Jellyfin's rule (M3uParser.GetChannelName), so the name matches what
    /// Jellyfin's clients show: the text after the last comma of the whole
    /// line, attributes included, less a leading "84." style number; else
    /// tvg-name, else tvg-id.
    /// </summary>
    private static string? ChannelName(string extInf, Dictionary<string, string> attributes)
    {
        var name = NameInExtInf(extInf);
        if (LeadingNumber(name, out var rest) is not null)
        {
            name = rest;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            attributes.TryGetValue("tvg-name", out name);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            attributes.TryGetValue("tvg-id", out name);
        }

        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    /// Jellyfin's rule (M3uParser.GetChannelNumber), less its last resort of a
    /// number as the URL's file name: tvg-chno, else a numeric tvg-id or
    /// channel-id, else the number leading the name ("84. VOX").
    /// </summary>
    private static string? ChannelNumber(string extInf, Dictionary<string, string> attributes)
    {
        foreach (var key in new[] { "tvg-chno", "tvg-id", "channel-id" })
        {
            if (attributes.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return value.Trim();
            }
        }

        return LeadingNumber(NameInExtInf(extInf), out _);
    }

    /// <summary>The text after the last comma of an #EXTINF line, attributes included, as Jellyfin reads it.</summary>
    private static string? NameInExtInf(string extInf)
    {
        var parts = extInf.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[^1].Trim() : null;
    }

    /// <summary>The "84" of "84. VOX" or "84.0 - VOX", with the rest of the name; null when the name does not start with a number.</summary>
    private static string? LeadingNumber(string? name, out string rest)
    {
        rest = name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var space = name.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0)
        {
            return null;
        }

        var number = name.AsSpan(0, space).Trim(" .".AsSpan());
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        rest = name[(space + 1)..].Trim(' ', '-');
        return number.ToString();
    }

    private static int? ArchiveDays(Dictionary<string, string> attributes)
    {
        foreach (var key in new[] { "catchup-days", "timeshift", "tvg-rec" })
        {
            if (attributes.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            {
                return days;
            }
        }

        return null;
    }

    private static bool SameNumber(string? a, string b)
    {
        if (a is null)
        {
            return false;
        }

        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return Math.Abs(x - y) < 0.0001;
        }

        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
