using Jellyfin.Plugin.Comseg.Segments;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class RecordingPathsTests
{
    [Fact]
    public void AFileUnderARootIsARecording()
    {
        Assert.True(RecordingPaths.IsUnder("/dvr/Show/Season 1/ep.ts", new[] { "/dvr/" }));
        Assert.True(RecordingPaths.IsUnder("/dvr/ep.ts", new[] { "/other", "/dvr" }));
    }

    [Fact]
    public void ASiblingWithTheSamePrefixIsNot()
    {
        Assert.False(RecordingPaths.IsUnder("/dvr2/ep.ts", new[] { "/dvr" }));
        Assert.False(RecordingPaths.IsUnder("/dvr", new[] { "/dvr" }));
        Assert.False(RecordingPaths.IsUnder(string.Empty, new[] { "/dvr" }));
    }

    [Fact]
    public void ExtraPathsSplitOnLinesAndSemicolons()
    {
        var paths = RecordingPaths.Split(" /a \n/b;\r\n\n/c ");
        Assert.Equal(new[] { "/a", "/b", "/c" }, paths);
    }
}
