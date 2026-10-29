using System.Collections.Generic;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Live;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class LiveBreakTrackerTests
{
    [Fact]
    public void ABreakIsPublishedOnlyAfterTheConfirmingPass()
    {
        var tracker = new LiveBreakTracker(2, 20);
        tracker.Observe(new List<Break> { new(100, 160) });
        Assert.Empty(tracker.Confirmed);
        tracker.Observe(new List<Break> { new(100, 220) });
        var b = Assert.Single(tracker.Confirmed);
        Assert.Equal(100, b.Start);
        Assert.Equal(220, b.End);
    }

    [Fact]
    public void AOnePassBreakVanishesWithoutBeingPublished()
    {
        var tracker = new LiveBreakTracker(2, 20);
        tracker.Observe(new List<Break> { new(100, 220), new(1311, 1323) });
        tracker.Observe(new List<Break> { new(100, 220) });
        var b = Assert.Single(tracker.Confirmed);
        Assert.Equal(100, b.Start);
    }

    [Fact]
    public void TheEndFollowsTheLatestAnalysis()
    {
        var tracker = new LiveBreakTracker(2, 20);
        tracker.Observe(new List<Break> { new(100, 160) });
        tracker.Observe(new List<Break> { new(100, 220) });
        tracker.Observe(new List<Break> { new(100, 280) });
        Assert.Equal(280, Assert.Single(tracker.Confirmed).End);
    }

    [Fact]
    public void AStartThatMovesWithinToleranceStillCounts()
    {
        var tracker = new LiveBreakTracker(2, 20);
        tracker.Observe(new List<Break> { new(100.0, 160) });
        tracker.Observe(new List<Break> { new(101.2, 220) });
        var b = Assert.Single(tracker.Confirmed);
        Assert.Equal(101.2, b.Start);
    }

    [Fact]
    public void ShortBreaksAreNotPublished()
    {
        var tracker = new LiveBreakTracker(1, 20);
        tracker.Observe(new List<Break> { new(100, 110), new(300, 400) });
        var b = Assert.Single(tracker.Confirmed);
        Assert.Equal(300, b.Start);
    }

    [Fact]
    public void AConfirmedBreakSurvivesOneAnalysisWithoutItAndGoesAfterTwo()
    {
        var tracker = new LiveBreakTracker(2, 20);
        tracker.Observe(new List<Break> { new(100, 220) });
        tracker.Observe(new List<Break> { new(100, 220) });
        Assert.Single(tracker.Confirmed);
        tracker.Observe(new List<Break>());
        Assert.Single(tracker.Confirmed);
        tracker.Observe(new List<Break>());
        Assert.Empty(tracker.Confirmed);
    }

    [Fact]
    public void ABreakReportedAgainAfterOneMissIsNotRestarted()
    {
        var tracker = new LiveBreakTracker(2, 20);
        tracker.Observe(new List<Break> { new(100, 220) });
        tracker.Observe(new List<Break> { new(100, 220) });
        tracker.Observe(new List<Break>());
        tracker.Observe(new List<Break> { new(100, 220) });
        Assert.Single(tracker.Confirmed);
    }

    [Fact]
    public void AnUnconfirmedBreakIsDroppedTheMomentItIsMissing()
    {
        var tracker = new LiveBreakTracker(2, 20);
        tracker.Observe(new List<Break> { new(100, 220) });
        tracker.Observe(new List<Break>());
        tracker.Observe(new List<Break> { new(100, 220) });
        Assert.Empty(tracker.Confirmed);
    }
}
