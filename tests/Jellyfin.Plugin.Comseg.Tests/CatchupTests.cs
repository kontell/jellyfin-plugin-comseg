using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Jellyfin.Plugin.Comseg.Catchup;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class CatchupTests
{
    private const string Playlist =
        "#EXTM3U\r\n"
        + "#EXTINF:-1 tvg-id=\"itv1.uk\" tvg-chno=\"101\" timeshift=\"7\" group-title=\"Entertainment\",ITV 1\r\n"
        + "http://tv.example/ch0001/index.m3u8?token=a\r\n"
        + "#EXTINF:-1 tvg-chno=\"104\" timeshift=\"7\",ITV 4\r\n"
        + "#EXTVLCOPT:http-user-agent=x\r\n"
        + "http://tv.example/ch0002/mono.m3u8?token=b\r\n"
        + "#EXTINF:-1 tvg-chno=\"24\" timeshift=\"0\",84. Sky Sports UHD 1\r\n"
        + "http://tv.example/ch0003/index.m3u8?token=c\r\n"
        + "#EXTINF:-1 tvg-name=\"Named Only\",\r\n"
        + "https://other.example/live/stream.m3u8\r\n"
        + "#EXTINF:-1 tvg-chno=\"200\",ITV 1\r\n"
        + "http://tv.example/ch0009/index.m3u8?token=d\r\n";

    [Fact]
    public void PlaylistEntriesAreReadLikeJellyfinReadsThem()
    {
        var entries = Archive.ParsePlaylist(Playlist);
        Assert.Equal(5, entries.Count);
        Assert.Equal(new PlaylistEntry("ITV 1", "101", 7, "http://tv.example/ch0001/index.m3u8?token=a"), entries[0]);
        Assert.Equal("ITV 4", entries[1].Name);
        Assert.Equal("http://tv.example/ch0002/mono.m3u8?token=b", entries[1].Url);

        // A leading number is dropped from the name, as Jellyfin drops it.
        Assert.Equal("Sky Sports UHD 1", entries[2].Name);
        Assert.Equal(0, entries[2].ArchiveDays);
        Assert.Equal("Named Only", entries[3].Name);
        Assert.Null(entries[3].Number);
    }

    [Fact]
    public void TheNameDecidesAndTheNumberBreaksTies()
    {
        var entries = Archive.ParsePlaylist(Playlist);
        Assert.Equal("ch0002", Archive.Find(entries, "itv 4", null)!.Url.Split('/')[3]);

        // Two "ITV 1" entries: the number picks one, and without it there is no answer.
        Assert.Equal("101", Archive.Find(entries, "ITV 1", "101")!.Number);
        Assert.Equal("200", Archive.Find(entries, "ITV 1", "200.0")!.Number);
        Assert.Null(Archive.Find(entries, "ITV 1", null));

        // A name the playlist does not have falls back to the number.
        Assert.Equal("ITV 4", Archive.Find(entries, "My ITV4", "104")!.Name);
        Assert.Null(Archive.Find(entries, "Nothing", "999"));
    }

    [Fact]
    public void ANumberLeadingTheNameIsTheChannelNumberWhenNoAttributeGivesOne()
    {
        var entries = Archive.ParsePlaylist("#EXTM3U\n#EXTINF:-1,84. News\nhttp://tv.example/ch0004/index.m3u8\n#EXTINF:-1 tvg-chno=\"7\",85.0 - Sport\nhttp://tv.example/ch0005/index.m3u8\n");
        Assert.Equal(new PlaylistEntry("News", "84", null, "http://tv.example/ch0004/index.m3u8"), entries[0]);
        Assert.Equal("News", Archive.Find(entries, "Renamed", "84")!.Name);

        // An attribute still comes first, as in Jellyfin.
        Assert.Equal("Sport", entries[1].Name);
        Assert.Equal("7", entries[1].Number);
    }

    [Fact]
    public void TheArchiveUrlKeepsTheChannelPathAndToken()
    {
        Assert.Equal(
            "http://tv.example/ch0001/archive-1794663592-10218.m3u8?token=a",
            Archive.ArchiveUrl("http://tv.example/ch0001/index.m3u8?token=a", 1794663592, 10218));
        Assert.Equal(
            "http://tv.example/ch0002/archive-100-60.m3u8",
            Archive.ArchiveUrl("http://tv.example/ch0002/mono.m3u8", 100, 60));
        Assert.Null(Archive.ArchiveUrl("http://tv.example/live/user/pass/1477.ts", 100, 60));
        Assert.Null(Archive.ArchiveUrl("http://tv.example/ch0001/index.m3u8", 100, 0));
    }

    private static readonly DateTime Asked = new(2026, 11, 14, 13, 39, 50, DateTimeKind.Utc);
    private static readonly DateTime Wanted = new(2026, 11, 14, 13, 40, 0, DateTimeKind.Utc);
    private const string Requested = "http://tv.example/ch0001/archive-1794663590-10210.m3u8?token=a";

    private static string Hls(DateTime first, int segments, bool endList = true)
    {
        var text = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:8\n#EXT-X-PLAYLIST-TYPE:EVENT\n");
        text.Append("#EXT-X-PROGRAM-DATE-TIME:").Append(first.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        for (var i = 0; i < segments; i++)
        {
            text.Append("#EXTINF:7.68,\nseg").Append(i).Append(".ts?token=a\n");
        }

        return text.Append(endList ? "#EXT-X-ENDLIST\n" : string.Empty).ToString();
    }

    [Fact]
    public void AWindowThatCoversTheProgrammePasses()
    {
        // 1330 segments of 7.68 s cover 10214 s from 13:39:53.
        var check = Archive.Check(Requested, Requested, Hls(Asked.AddSeconds(3.857), 1330), Asked, Wanted, 10200, 15);
        Assert.Empty(check.Problems);
        Assert.Equal(Asked.AddSeconds(3.857), check.FirstSegmentUtc);
        Assert.Equal(1330 * 7.68, check.TotalSeconds, 3);
    }

    [Fact]
    public void TheFailuresTheArchiveAnswersWith200AreCaught()
    {
        // A wrong token or a time outside the archive: a stub elsewhere on the server.
        var stub = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:5.9,\n/stub/s0.ts\n#EXT-X-ENDLIST\n";
        var redirected = Archive.Check(Requested, "http://tv.example/new_stub/channel.m3u8", stub, Asked, Wanted, 10200, 15);
        Assert.Contains(redirected.Problems, p => p.Contains("redirected", StringComparison.Ordinal));

        // A start in the future: the live playlist, without an end.
        var live = Archive.Check(Requested, Requested, Hls(Asked.AddHours(5), 6, endList: false), Asked, Wanted, 10200, 15);
        Assert.Contains(live.Problems, p => p.Contains("no end", StringComparison.Ordinal));
        Assert.Contains(live.Problems, p => p.Contains("starts at", StringComparison.Ordinal));

        // A window that runs past now: cut short, with an end.
        var cut = Archive.Check(Requested, Requested, Hls(Asked.AddSeconds(3), 100), Asked, Wanted, 10200, 15);
        Assert.Single(cut.Problems);
        Assert.Contains("of the 10200 s", cut.Problems[0], StringComparison.Ordinal);

        Assert.Contains("not a playlist", Archive.Check(Requested, Requested, "<html>", Asked, Wanted, 60, 15).Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutPaddingTheFirstSegmentMayStartAfterTheProgramme()
    {
        // Asked for 13:40:00 exactly: the archive begins 5 s later, which the slack allows.
        var check = Archive.Check(Requested, Requested, Hls(Wanted.AddSeconds(5.165), 18), Wanted, Wanted, 120, 15);
        Assert.Empty(check.Problems);
    }

    [Fact]
    public void FilesAreLaidOutLikeJellyfinsOwnRecordings()
    {
        static string Valid(string name) => name.Replace(':', ' ');
        var start = new DateTime(2026, 11, 14, 13, 40, 0, DateTimeKind.Utc);
        var (folder, fileBase) = RecordingFiles.Paths("/dvr", "Wales v New Zealand: Nations Championship", start, Valid);
        Assert.Equal("/dvr/Wales v New Zealand  Nations Championship", folder);
        var stamp = start.ToLocalTime().ToString("yyyy_MM_dd_HH_mm_ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(folder + "/Wales v New Zealand  Nations Championship " + stamp, fileBase);
    }

    [Fact]
    public void ATitleThatIsNotOneFolderIsRefused()
    {
        static string Valid(string name) => name.Replace("?", string.Empty, StringComparison.Ordinal);
        foreach (var title in new[] { "..", ".", "...", ". . .", " .. . ", "???", " ? ", "a/b", @"a\b" })
        {
            Assert.Null(RecordingFiles.FolderName(title, Valid));
        }

        Assert.Equal("Match of the Day", RecordingFiles.FolderName(" Match of the Day? ", Valid));
        Assert.Equal("...And Justice for All", RecordingFiles.FolderName("...And Justice for All", Valid));
        Assert.Throws<ArgumentException>(() => RecordingFiles.Paths("/dvr", "..", DateTime.UtcNow, Valid));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.5", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("64:ff9b:1::1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("100::1", false)]
    [InlineData("2001::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("5f00::1", false)]
    [InlineData("2002:c0a8:0101::1", false)]
    [InlineData("2002:0808:0808::1", true)]
    [InlineData("2606:4700::1111", true)]
    public void OnlyPublicAddressesMayBeFetched(string address, bool expected)
    {
        Assert.Equal(expected, PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public void TheNfoFilesHaveJellyfinsShape()
    {
        var start = new DateTime(2026, 11, 14, 13, 40, 0, DateTimeKind.Utc);
        var movie = RecordingFiles.MovieNfo("A & B: Final", "Live <coverage>.", start, "/dvr/A/poster.jpg");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, movie.Take(3).ToArray());
        var doc = XDocument.Parse(Encoding.UTF8.GetString(movie, 3, movie.Length - 3));
        Assert.Equal("movie", doc.Root!.Name.LocalName);
        Assert.Equal("A & B: Final", doc.Root.Element("title")!.Value);
        Assert.Equal("Live <coverage>.", doc.Root.Element("plot")!.Value);
        Assert.Equal("true", doc.Root.Element("lockdata")!.Value);
        Assert.Equal("/dvr/A/poster.jpg", doc.Root.Element("art")!.Element("poster")!.Value);
        Assert.Equal(start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), doc.Root.Element("dateadded")!.Value);

        var file = RecordingFiles.FileNfo("A", null, start);
        var fileDoc = XDocument.Parse(Encoding.UTF8.GetString(file, 3, file.Length - 3));
        Assert.Equal(new[] { "title", "dateadded", "plot", "lockdata" }, fileDoc.Root!.Elements().Select(e => e.Name.LocalName));
        Assert.Contains("standalone=\"yes\"", Encoding.UTF8.GetString(file), StringComparison.Ordinal);
    }
}
