using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Configuration;
using Jellyfin.Plugin.Comseg.Profiles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class ProfileTests
{
    private static readonly ChannelProfile Rte = new()
    {
        Name = "RTÉ",
        Channels = "RTÉ One; RTÉ Two",
        IniKeys = "ticker_tape_percentage=0",
    };

    private static readonly ChannelProfile Sky = new()
    {
        Name = "Sky Sports",
        Channels = "Sky*\nPremier Sports 1",
        IniKeys = "restart_logo_search=0\n# a comment\nlogo_threshold = 0.8",
        AnalysisHeight = 480,
        LiveDetection = false,
        KeepFrames = true,
    };

    private static RecordingRecord Record(string channel)
        => new("/dvr/A/A 2026_11_19_21_00_00.ts", channel, null, "A", null, "timer");

    [Fact]
    public void WithoutAProfileTheIniIsExactlyTheOneRunsUsedBefore()
    {
        var config = new PluginConfiguration();
        var baseText = ComskipIni.DefaultText();
        Assert.Equal(
            ComskipIni.Compose(baseText, ComskipIni.CompletedOverrides(config)),
            ComskipIni.Compose(baseText, new Dictionary<string, string>(), ComskipIni.CompletedOverrides(config)));
        Assert.Equal(
            ComskipIni.Compose(baseText, ComskipIni.LiveOverrides(config)),
            ComskipIni.Compose(baseText, ProfileRules.Resolve(config, Record("ITV 1")).Keys, ComskipIni.LiveOverrides(config)));
    }

    [Fact]
    public void ProfileKeysReplaceTheBaseAndTheRunModeKeysStillWin()
    {
        var composed = ComskipIni.Compose(
            "ticker_tape_percentage=15\nthread_count=2\nverbose=0\n",
            new Dictionary<string, string> { ["ticker_tape_percentage"] = "0", ["thread_count"] = "9" },
            new Dictionary<string, string> { ["thread_count"] = "4", ["live_tv"] = "0" });
        var keys = composed.Split('\n').Select(ComskipIni.KeyOf).Where(k => k is not null).ToList();

        // Comskip reads the first line for a key, so each key must appear once.
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains("ticker_tape_percentage=0\n", composed, StringComparison.Ordinal);
        Assert.Contains("thread_count=4\n", composed, StringComparison.Ordinal);
        Assert.DoesNotContain("thread_count=9", composed, StringComparison.Ordinal);
        Assert.Contains("verbose=0\n", composed, StringComparison.Ordinal);
        Assert.True(composed.IndexOf("; channel profile", StringComparison.Ordinal) < composed.IndexOf("; comseg run settings", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("RTE One", "RTÉ")]
    [InlineData("rté two", "RTÉ")]
    [InlineData("Sky Main UHD", "Sky Sports")]
    [InlineData("Premier Sports 1", "Sky Sports")]
    [InlineData("Premier Sports 2", null)]
    [InlineData("ITV 1", null)]
    [InlineData("", null)]
    public void TheFirstProfileThatListsTheChannelApplies(string channel, string? profile)
    {
        var shadow = new ChannelProfile { Name = "late", Channels = "RTE*" };
        Assert.Equal(profile, ProfileRules.Match(new[] { Rte, Sky, shadow }, channel)?.Name);
    }

    [Fact]
    public void AProfileWithoutChannelsNeverApplies()
    {
        Assert.Null(ProfileRules.Match(new[] { new ChannelProfile { Name = "empty", Channels = " ; " } }, "ITV 1"));
        Assert.Contains("empty: no channels, so it never applies", ProfileRules.Check(new[] { new ChannelProfile { Name = "empty" } }));
    }

    [Fact]
    public void KeysAreReadAndWhatCannotBeUsedIsSaid()
    {
        var (keys, problems) = ProfileRules.ParseKeys("ticker_tape_percentage=0; logo_threshold = 0.8\n# note\nlive_tv=0;Bad Key=1;noequals");
        Assert.Equal(new Dictionary<string, string> { ["ticker_tape_percentage"] = "0", ["logo_threshold"] = "0.8" }, keys);
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("live_tv is set by the plugin", StringComparison.Ordinal));
        Assert.Contains("\"Bad Key=1\" is not a key=value pair", problems);
        Assert.Contains("\"noequals\" is not a key=value pair", problems);
    }

    [Fact]
    public void AProfileChangesOnlyItsOwnRecordingsSettings()
    {
        var config = new PluginConfiguration { AnalysisHeight = 720, Profiles = new[] { Rte, Sky } };

        var sky = ProfileRules.Resolve(config, Record("Sky Action"));
        Assert.Equal("Sky Sports", sky.ProfileName);
        Assert.Equal(480, sky.Config.AnalysisHeight);
        Assert.Equal(720, config.AnalysisHeight);
        Assert.False(sky.LiveDetection);
        Assert.True(sky.KeepFrames);
        Assert.Equal("0", sky.Keys["restart_logo_search"]);

        var rte = ProfileRules.Resolve(config, Record("RTE One"));
        Assert.Same(config, rte.Config);
        Assert.True(rte.LiveDetection);
        Assert.Equal("channel RTE One, profile RTÉ", rte.Description);

        var itv = ProfileRules.Resolve(config, Record("ITV 1"));
        Assert.Null(itv.Profile);
        Assert.Empty(itv.Keys);
        Assert.Equal("channel ITV 1, default settings", itv.Description);

        var unknown = ProfileRules.Resolve(config, null);
        Assert.Null(unknown.Profile);
        Assert.Equal("channel unknown, default settings", unknown.Description);
    }

    [Fact]
    public void RecordsAreKeptByPathAndSweptWhenTheRecordingIsGone()
    {
        var root = Path.Combine(Path.GetTempPath(), "comseg-records-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var records = new RecordingRecords(new FakePaths(root), NullLogger<RecordingRecords>.Instance);
            var media = Path.Combine(root, "dvr", "A", "A 2026_11_19_21_00_00.ts");
            Directory.CreateDirectory(Path.GetDirectoryName(media)!);
            File.WriteAllText(media, string.Empty);

            // On a drive that is not there, nothing is swept.
            var unmounted = Path.Combine(root, "gone", "dvr", "B", "B.ts");
            records.Save(new RecordingRecord(unmounted, "ITV 1", null, null, null, "timer"));
            var start = new DateTime(2026, 11, 19, 21, 0, 0, DateTimeKind.Utc);

            Assert.Null(records.Get(media));
            Assert.Equal(SaveResult.Saved, records.SaveIfMissing(new RecordingRecord(media, "RTE One", "c1", "News", start, "timer")));

            // A record already there, an admin's say, is kept.
            records.Save(new RecordingRecord(media, "RTE Two", null, "News", start, "admin"));
            Assert.Equal(SaveResult.Existed, records.SaveIfMissing(new RecordingRecord(media, "RTE One", "c1", "News", start, "timer")));
            Assert.Equal(new RecordingRecord(media, "RTE Two", null, "News", start, "admin"), records.Get(media));
            Assert.Null(records.Get(media + ".other"));

            // Swept only under a recording root that is there.
            var roots = new[] { Path.Combine(root, "dvr"), Path.Combine(root, "gone", "dvr") };
            Assert.Equal(0, records.Sweep(roots));
            File.Delete(media);
            Assert.Equal(0, records.Sweep([Path.Combine(root, "elsewhere")]));
            Assert.Equal(1, records.Sweep(roots));
            Assert.Null(records.Get(media));
            Assert.NotNull(records.Get(unmounted));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
