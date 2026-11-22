using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Catchup;
using Jellyfin.Plugin.Comseg.Configuration;
using Jellyfin.Plugin.Comseg.Profiles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class PlaylistGroupTests
{
    private const string Playlist =
        "#EXTM3U\n"
        + "#EXTINF:-1 tvg-id=\"SkySportsMainEvent.uk\" group-title=\"Sport;Sky Sports\", Sky Main Event\n"
        + "http://tv.example/ch0001/index.m3u8?token=a\n"
        + "#EXTINF:-1 group-title=\"Sport\", Premier Sports 1\n"
        + "http://tv.example/ch0002/index.m3u8?token=b\n"
        + "#EXTINF:-1, RTE One\n"
        + "http://tv.example/ch0003/index.m3u8?token=c\n";

    private static readonly ChannelProfile[] Profiles =
    [
        new() { Name = "Sky Sports", Groups = "sky sports" },
        new() { Name = "TNT", Groups = "TNT*" },
        new() { Name = "ITV", Channels = "ITV*" },
        new() { Name = "Sport", Groups = "Sport" },
    ];

    [Fact]
    public void AChannelIsInEachGroupOfItsGroupTitle()
    {
        var entries = Archive.ParsePlaylist(Playlist);
        Assert.Equal("Sport;Sky Sports", entries[0].GroupTitle);
        Assert.Equal(["Sport", "Sky Sports"], entries[0].Groups);
        Assert.Equal(["Sport"], entries[1].Groups);
        Assert.Empty(entries[2].Groups);
        Assert.Equal(["Sport", "Sky Sports"], new PlaylistEntry("x", null, null, "u", " Sport ; ;Sky Sports ").Groups);
    }

    [Theory]
    [InlineData("Sky Main Event", "Sport;Sky Sports", "Sky Sports")]
    [InlineData("TNT Sports 1", "Sport;TNT Sports", "TNT")]
    [InlineData("Premier Sports 1", "Sport", "Sport")]
    [InlineData("ITV 1", "TV", "ITV")]
    [InlineData("RTE One", "TV", null)]
    [InlineData("RTE One", "", null)]
    [InlineData("Sky Main Event", null, null)]
    public void TheFirstProfileThatListsTheChannelOrOneOfItsGroupsApplies(string channel, string? groupTitle, string? profile)
    {
        // Groups not known (a record from before them, or a playlist that could not be read): the name alone decides.
        var groups = groupTitle is null ? null : new PlaylistEntry(channel, null, null, "u", groupTitle).Groups;
        Assert.Equal(profile, ProfileRules.Match(Profiles, channel, groups)?.Name);
    }

    [Fact]
    public void ARecordingRunsWithTheProfileOfItsChannelsGroup()
    {
        var config = new PluginConfiguration { Profiles = Profiles };
        var record = new RecordingRecord("/dvr/A/A.ts", "Sky Main Event", null, "A", null, "timer", ["Sport", "Sky Sports"]);
        Assert.Equal("Sky Sports", ProfileRules.Resolve(config, record).ProfileName);
        Assert.Null(ProfileRules.Resolve(config, record with { ChannelGroups = null }).ProfileName);
    }

    [Fact]
    public void AProfileWithGroupsAloneIsNotReportedAsEmpty()
    {
        Assert.Empty(ProfileRules.Check([new ChannelProfile { Name = "Sky Sports", Groups = "Sky Sports" }]));
        Assert.Contains("empty: no channels or playlist groups, so it never applies", ProfileRules.Check([new ChannelProfile { Name = "empty", Groups = " ; " }]));
    }

    [Fact]
    public void ARecordKeepsItsGroupsAndAnOlderRecordReadsWithout()
    {
        var root = Path.Combine(Path.GetTempPath(), "comseg-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var records = new RecordingRecords(new FakePaths(root), NullLogger<RecordingRecords>.Instance);
            var media = Path.Combine(root, "dvr", "A.ts");
            records.Save(new RecordingRecord(media, "Sky Main Event", "c1", "A", null, "timer", ["Sport", "Sky Sports"]));
            Assert.Equal(["Sport", "Sky Sports"], records.Get(media)!.ChannelGroups!);

            // As a record written before groups were noted.
            var file = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Single();
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Assert.True(json.Remove("ChannelGroups"));
            File.WriteAllText(file, json.ToJsonString());
            var old = records.Get(media);
            Assert.NotNull(old);
            Assert.Equal("Sky Main Event", old.ChannelName);
            Assert.Null(old.ChannelGroups);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TheTunersPlaylistGivesAChannelsGroups()
    {
        var root = Path.Combine(Path.GetTempPath(), "comseg-tuner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "tuner.m3u8");
            var config = new FakeConfig(new TunerHostInfo { Id = "t1", Type = "m3u", Url = file });
            var playlists = new TunerPlaylists(config, new NoHttp(), NullLogger<TunerPlaylists>.Instance);

            // A playlist that cannot be read leaves the groups unknown, and is read again next time.
            Assert.Null(await playlists.GroupsAsync("Sky Main Event", CancellationToken.None));
            File.WriteAllText(file, Playlist);
            Assert.Equal(["Sport", "Sky Sports"], (await playlists.GroupsAsync("sky main event", CancellationToken.None))!);
            Assert.Empty((await playlists.GroupsAsync("RTE One", CancellationToken.None))!);
            Assert.Null(await playlists.GroupsAsync("BBC One", CancellationToken.None));
            Assert.Equal("no tuner playlist has the channel BBC One (1)", (await playlists.FindAsync("BBC One", "1", CancellationToken.None)).Problem);

            var none = new TunerPlaylists(new FakeConfig(new TunerHostInfo { Id = "h", Type = "hdhomerun", Url = "http://hd" }), new NoHttp(), NullLogger<TunerPlaylists>.Instance);
            Assert.Equal("Jellyfin has no M3U tuner", (await none.FindAsync("Sky Main Event", null, CancellationToken.None)).Problem);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no HTTP in these tests");
    }

    private sealed class FakeConfig : IConfigurationManager
    {
        private readonly LiveTvOptions _liveTv;

        public FakeConfig(params TunerHostInfo[] tuners) => _liveTv = new LiveTvOptions { TunerHosts = tuners };

#pragma warning disable CS0067 // The events are part of the interface; nothing here raises them.
        public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdating;

        public event EventHandler<EventArgs>? ConfigurationUpdated;

        public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdated;
#pragma warning restore CS0067

        public IApplicationPaths CommonApplicationPaths => throw new NotSupportedException();

        public BaseApplicationConfiguration CommonConfiguration => throw new NotSupportedException();

        public object GetConfiguration(string key) => key == "livetv" ? _liveTv : throw new NotSupportedException(key);

        public void SaveConfiguration() => throw new NotSupportedException();

        public void ReplaceConfiguration(BaseApplicationConfiguration newConfiguration) => throw new NotSupportedException();

        public void RegisterConfiguration<T>()
            where T : IConfigurationFactory => throw new NotSupportedException();

        public ConfigurationStore[] GetConfigurationStores() => throw new NotSupportedException();

        public Type GetConfigurationType(string key) => throw new NotSupportedException();

        public void SaveConfiguration(string key, object configuration) => throw new NotSupportedException();

        public void AddParts(IEnumerable<IConfigurationFactory> factories) => throw new NotSupportedException();
    }
}
