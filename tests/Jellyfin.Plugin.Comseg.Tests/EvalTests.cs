using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Eval;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class EvalTests
{
    private const string ReferenceText = """
        # one break, and the programme after it looked at from end to end
        duration 1000
        unknown 0 50
        break 300 500
        free 500 900
        """;

    [Fact]
    public void AReferenceIsReadLineByLine()
    {
        var reference = Reference.Parse(ReferenceText);
        Assert.Equal(1000, reference.Duration);
        Assert.Equal(new Break(300, 500), Assert.Single(reference.Breaks));
        Assert.Equal(new Break(500, 900), Assert.Single(reference.Free));
        Assert.True(reference.IsUnknown(10));
        Assert.True(reference.IsBreak(300));
        Assert.False(reference.IsBreak(500));
    }

    [Theory]
    [InlineData("break 300 500")]
    [InlineData("duration 100\nbreak 50 40")]
    [InlineData("duration 100\nadvert 10 20")]
    public void AReferenceThatCannotBeTrustedIsRefused(string text)
    {
        Assert.Throws<FormatException>(() => Reference.Parse(text));
    }

    [Fact]
    public void TheReplayPublishesABreakAtThePollAfterItsConfirmingAnalysis()
    {
        var analyses = new List<LiveSim.Analysis>
        {
            new(360, new List<Break> { new(300, 350) }),
            new(420, new List<Break> { new(300, 410) }),
            new(480, new List<Break> { new(300, 470) }),
        };
        var entries = LiveSim.Run(analyses, 15, 2, 20, 600);
        Assert.Equal(2, entries.Count);
        Assert.Equal(420, entries[0].T);
        Assert.Equal(new Break(300, 410), Assert.Single(entries[0].Rows));
        Assert.Equal(480, entries[1].T);
        Assert.Equal(new Break(300, 470), Assert.Single(entries[1].Rows));
    }

    [Fact]
    public void TwoAnalysesBetweenPollsCountAsOne()
    {
        // The watcher reads the file as it stands at each poll, so the first of
        // two analyses written inside one poll is never seen.
        var analyses = new List<LiveSim.Analysis>
        {
            new(361, new List<Break> { new(300, 350) }),
            new(365, new List<Break> { new(300, 355) }),
            new(420, new List<Break> { new(300, 410) }),
        };
        var entries = LiveSim.Run(analyses, 15, 3, 20, 600);
        Assert.Empty(entries);
    }

    [Fact]
    public void ALateRowCostsOnlyTheViewersItWasLateFor()
    {
        var reference = Reference.Parse(ReferenceText);

        // The row appears 100 s after the break began and then keeps pace with it.
        var entries = new List<Timeline.Entry>
        {
            new(400, new List<Break> { new(300, 400) }),
            new(520, new List<Break> { new(300, 500) }),
        };
        var timeline = new Timeline(entries, new List<Break> { new(300, 500) }, 1240);
        var result = Scorer.Score(reference, timeline, new double[] { 30, 600 });
        Assert.False(result.Failed);
        Assert.Equal(0, result.Replacements);

        var near = result.Viewers.Single(v => v.Name == "live+30s");
        Assert.Equal(0, near.ProgrammeLost);

        // 30 s behind: seconds 300 to 370 pass before the row exists, and from 400
        // to 490 the row in force still ends at 400.
        Assert.Equal(160, near.AdvertsLeft, 1);

        var far = result.Viewers.Single(v => v.Name == "live+600s");
        Assert.Equal(0, far.AdvertsLeft);
        Assert.Equal(200, far.AdvertsSkipped, 1);

        var final = result.Viewers.Single(v => v.Name == "final");
        Assert.Equal(0, final.AdvertsLeft + final.ProgrammeLost);
        Assert.Equal(160, result.Total, 1);
    }

    [Fact]
    public void ProgrammeSkippedCountsFiveTimesAndFailsInsideAFreeSpan()
    {
        var reference = Reference.Parse(ReferenceText);
        var rows = new List<Break> { new(300, 520) };
        var timeline = new Timeline(Array.Empty<Timeline.Entry>(), rows, 0);
        var result = Scorer.Score(reference, timeline, Array.Empty<double>());
        Assert.Equal(20, Assert.Single(result.Viewers).ProgrammeLost, 1);
        Assert.Equal(100, result.Total, 1);
        Assert.True(result.Failed);
        Assert.Contains("covers 20.0 s of programme", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownStretchesAreNotScored()
    {
        var reference = Reference.Parse(ReferenceText);
        var timeline = new Timeline(Array.Empty<Timeline.Entry>(), new List<Break> { new(0, 50), new(300, 500) }, 0);
        var result = Scorer.Score(reference, timeline, Array.Empty<double>());
        Assert.Equal(0, result.Total);
        Assert.False(result.Failed);
    }

    [Fact]
    public void ARowWithdrawnOrRestartedIsCounted()
    {
        var reference = Reference.Parse(ReferenceText);
        var entries = new List<Timeline.Entry>
        {
            new(100, new List<Break> { new(60, 90), new(300, 360) }),
            new(160, new List<Break> { new(300, 420) }),
            new(220, new List<Break> { new(310, 480) }),
        };
        var result = Scorer.Score(reference, new Timeline(entries, null, null), Array.Empty<double>());
        Assert.Equal(2, result.Replacements);
        Assert.Equal(2 * Scorer.ReplacementCost, result.Total);
    }

    [Fact]
    public void ATimelineSurvivesItsFile()
    {
        var entries = new List<Timeline.Entry> { new(420, new List<Break> { new(300.5, 410.25) }) };
        var path = Path.Combine(Path.GetTempPath(), "comseg-eval-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new Timeline(entries, new List<Break> { new(300.5, 500) }, 1240).Save(path);
            var loaded = Timeline.Load(path);
            Assert.Equal(new Break(300.5, 410.25), Assert.Single(loaded.At(430)));
            Assert.Empty(loaded.At(100));
            Assert.Equal(new Break(300.5, 500), Assert.Single(loaded.At(1240)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProfileKeysSitBetweenTheBaseIniAndTheRunMode()
    {
        var config = new Configuration.PluginConfiguration { ThreadCount = 2 };
        var keys = new Dictionary<string, string> { ["ticker_tape_percentage"] = "0", ["thread_count"] = "9" };
        var ini = FinalPass.Ini(config, keys, Comskip.ComskipIni.CompletedOverrides(config));
        var lines = ini.Split('\n');
        Assert.Contains("ticker_tape_percentage=0", lines);
        Assert.DoesNotContain("ticker_tape_percentage=15", lines);
        Assert.Contains("thread_count=2", lines);
        Assert.DoesNotContain("thread_count=9", lines);
    }
}
