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
        var args = ScaledCopy.Arguments("/rec/a.ts", "/work/copy.ts", 720, "/dev/dri/renderD128", null).ToList();
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
        var args = ScaledCopy.Arguments("/rec/a.ts", "/work/copy.ts", 480, "/dev/dri/renderD129", TimeSpan.FromMinutes(20)).ToList();
        Assert.Equal("1", args[args.IndexOf("-follow") + 1]);
        Assert.Equal("1200000000", args[args.IndexOf("-rw_timeout") + 1]);
        Assert.True(args.IndexOf("-rw_timeout") < args.IndexOf("-i"));
        Assert.Equal("/dev/dri/renderD129", args[args.IndexOf("-hwaccel_device") + 1]);
        Assert.Contains("scale_vaapi=w=-2:h='min(480,ih)':format=nv12", args);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(720, 720)]
    [InlineData(100, 240)]
    [InlineData(4000, 1080)]
    public void TheHeightIsOffOrWithinReason(int configured, int used)
        => Assert.Equal(used, ScaledCopy.Height(new PluginConfiguration { AnalysisHeight = configured }));
}
