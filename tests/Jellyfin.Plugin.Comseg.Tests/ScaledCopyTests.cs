using System;
using System.Linq;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class ScaledCopyTests
{
    [Fact]
    public void ACopyOfAFinishedRecordingReadsItOnceAndKeepsItsTimestamps()
    {
        var args = ScaledCopy.Arguments("/rec/a.ts", "/work/copy.ts", 720, "/dev/dri/renderD128", null, false).ToList();
        Assert.DoesNotContain("-follow", args);
        Assert.Equal("/rec/a.ts", args[args.IndexOf("-i") + 1]);
        Assert.Contains("scale_vaapi=w=-2:h='min(720,ih)':format=nv12", args);
        Assert.Contains("0:a?", args);
        Assert.Contains("-copyts", args);
        Assert.Equal("/work/copy.ts", args[^1]);
    }

    [Fact]
    public void ACopyOfARecordingInProgressWaitsOutAStallBeforeItEnds()
    {
        var args = ScaledCopy.Arguments("/rec/a.ts", "/work/copy.ts", 480, "/dev/dri/renderD129", TimeSpan.FromMinutes(20), false).ToList();
        Assert.Equal("1", args[args.IndexOf("-follow") + 1]);
        Assert.Equal("1200000000", args[args.IndexOf("-rw_timeout") + 1]);
        Assert.True(args.IndexOf("-rw_timeout") < args.IndexOf("-i"));
        Assert.Equal("/dev/dri/renderD129", args[args.IndexOf("-hwaccel_device") + 1]);
        Assert.Contains("scale_vaapi=w=-2:h='min(480,ih)':format=nv12", args);
    }

    [Fact]
    public void AnHdrRecordingsCopyIsToneMappedThroughVulkanBeforeItIsEncoded()
    {
        var args = ScaledCopy.Arguments("/rec/a.ts", "/work/copy.ts", 720, "/dev/dri/renderD128", null, true).ToList();
        Assert.Equal("drm=dr:/dev/dri/renderD128", args[args.IndexOf("-init_hw_device") + 1]);
        Assert.Contains("vulkan=vk@dr", args);
        Assert.Equal("vk", args[args.IndexOf("-filter_hw_device") + 1]);
        Assert.Equal("va", args[args.IndexOf("-hwaccel_device") + 1]);
        var filter = args[args.IndexOf("-vf") + 1];
        Assert.StartsWith("scale_vaapi=w=-2:h='min(720,ih)':format=p010,", filter);
        Assert.Contains("libplacebo=format=bgra:tonemapping=bt.2390", filter);
        Assert.EndsWith("scale_vaapi=format=nv12", filter);
        Assert.Equal("h264_vaapi", args[args.IndexOf("-c:v") + 1]);
    }

    [Theory]
    [InlineData(7200, 7195, true)]
    [InlineData(7200, 7000, false)]
    [InlineData(0, 7200, false)]
    [InlineData(7200, 0, false)]
    public void ACopyStandsInOnlyWhenBothDurationsShowItReachesTheEnd(double recording, double copy, bool covers)
        => Assert.Equal(covers, ScaledCopy.Covers(new ScaledCopy.Probe(1080, recording), new ScaledCopy.Probe(720, copy)));

    [Fact]
    public void ACopyThatCannotBeProbedDoesNotStandIn()
        => Assert.False(ScaledCopy.Covers(new ScaledCopy.Probe(1080, 7200), null));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(720, 720)]
    [InlineData(100, 240)]
    [InlineData(4000, 1080)]
    public void TheHeightIsOffOrWithinReason(int configured, int used)
        => Assert.Equal(used, ScaledCopy.Height(new PluginConfiguration { AnalysisHeight = configured }));
}
