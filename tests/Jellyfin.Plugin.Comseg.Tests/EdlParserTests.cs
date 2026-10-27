using Jellyfin.Plugin.Comseg.Edl;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class EdlParserTests
{
    [Fact]
    public void ParsesComskipLinesSortedByStart()
    {
        var breaks = EdlParser.Parse("818.02\t970.18\t0\n453.80\t648.46\t0\n");
        Assert.Equal(2, breaks.Count);
        Assert.Equal(453.80, breaks[0].Start, 2);
        Assert.Equal(648.46, breaks[0].End, 2);
        Assert.Equal(818.02, breaks[1].Start, 2);
    }

    [Fact]
    public void SkipsTornAndMalformedLines()
    {
        var breaks = EdlParser.Parse("100.0\t160.0\t0\n200.5\n\nabc\tdef\t0\n300.0\t2");
        Assert.Single(breaks);
        Assert.Equal(100.0, breaks[0].Start);
    }

    [Fact]
    public void DropsNonPositiveLengths()
    {
        var breaks = EdlParser.Parse("100\t100\t0\n120\t110\t0\n-5\t10\t0\n");
        Assert.Empty(breaks);
    }

    [Fact]
    public void AcceptsSpacesAndWindowsLineEnds()
    {
        var breaks = EdlParser.Parse("10 20 3\r\n30 40 3\r\n");
        Assert.Equal(2, breaks.Count);
        Assert.Equal(40.0, breaks[1].End);
    }
}
