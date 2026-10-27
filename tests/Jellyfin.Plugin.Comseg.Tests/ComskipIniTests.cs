using System.Collections.Generic;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class ComskipIniTests
{
    [Fact]
    public void AnOverrideReplacesEveryExistingLineForTheKey()
    {
        var composed = ComskipIni.Compose(
            "live_tv=0 ; off\nticker_tape=160\n  live_tv=1\n",
            new Dictionary<string, string> { ["live_tv"] = "1" });
        Assert.DoesNotContain("live_tv=0", composed);
        Assert.Contains("ticker_tape=160", composed);
        Assert.Equal(1, CountOf(composed, "live_tv="));
        Assert.Contains("live_tv=1", composed);
    }

    [Fact]
    public void CommentsAndSectionsAreKept()
    {
        var composed = ComskipIni.Compose(
            "; a comment\n[Main Settings]\ndetect_method=123\n",
            new Dictionary<string, string> { ["output_edl"] = "1" });
        Assert.Contains("; a comment", composed);
        Assert.Contains("[Main Settings]", composed);
        Assert.Contains("detect_method=123", composed);
        Assert.Contains("output_edl=1", composed);
    }

    [Fact]
    public void TheEmbeddedDefaultIgnoresTheBottomOfThePicture()
    {
        var text = ComskipIni.DefaultText();
        Assert.Contains("ticker_tape=160", text);
        Assert.Contains("detect_method=123", text);
    }

    [Fact]
    public void LiveOverridesCarryThePatchedBinarysInterval()
    {
        var overrides = ComskipIni.LiveOverrides(new PluginConfiguration { LiveMasterIntervalSeconds = 45, LiveTvRetries = 10, ThreadCount = 2 });
        Assert.Equal("1", overrides["live_tv"]);
        Assert.Equal("45", overrides["live_master_interval"]);
        Assert.Equal("10", overrides["live_tv_retries"]);
        Assert.Equal("2", overrides["thread_count"]);
        Assert.Equal("0", ComskipIni.CompletedOverrides(new PluginConfiguration())["live_tv"]);
    }

    [Theory]
    [InlineData("detect_method=123", "detect_method")]
    [InlineData("  padding = 0 ; x", "padding")]
    [InlineData("; comment", null)]
    [InlineData("[Logo]", null)]
    [InlineData("", null)]
    public void KeyOfReadsTheKey(string line, string? expected)
    {
        Assert.Equal(expected, ComskipIni.KeyOf(line));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
