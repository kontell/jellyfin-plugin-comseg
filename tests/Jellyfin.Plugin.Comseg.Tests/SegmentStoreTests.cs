using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Providers;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public sealed class SegmentStoreTests : IDisposable
{
    private const string Comseg = ComsegSegmentProvider.ProviderName;
    private const string Other = "Intro Skipper";

    private readonly string _data = Directory.CreateTempSubdirectory("comseg-tests-").FullName;
    private readonly FakeSegments _segments = new(Comseg, Other);
    private readonly Movie _item = new() { Id = Guid.NewGuid(), Name = "Recording" };
    private readonly Workspace _workspace;
    private readonly SegmentStore _store;

    public SegmentStoreTests()
    {
        _workspace = new Workspace(new FakePaths(_data));
        _store = new SegmentStore(_segments, _workspace, NullLogger<SegmentStore>.Instance);
    }

    public void Dispose() => Directory.Delete(_data, true);

    [Fact]
    public async Task AnotherProvidersRowsAreLeftAlone()
    {
        _segments.Add(_item.Id, Other, 100, 160);
        _segments.Add(_item.Id, Comseg, 300, 400);

        await _store.PublishAsync(_item, new List<Break> { new(100.2, 160.1) }, "test", final: true);

        Assert.Contains(_segments.Rows, r => r.Provider == Other && r.Start == 100 && r.End == 160);
        var ours = Assert.Single(_segments.Rows, r => r.Provider == Comseg);
        Assert.Equal(100.2, ours.Start, 3);
    }

    [Fact]
    public async Task OnlyWhatDiffersIsReplaced()
    {
        var kept = _segments.Add(_item.Id, Comseg, 100, 160);
        var moved = _segments.Add(_item.Id, Comseg, 300, 400);

        var changes = await _store.PublishAsync(_item, new List<Break> { new(100, 160), new(300, 430) }, "test", final: false);

        Assert.Equal(2, changes);
        Assert.Contains(_segments.Rows, r => r.Id == kept.Id);
        Assert.DoesNotContain(_segments.Rows, r => r.Id == moved.Id);
        Assert.Contains(_segments.Rows, r => r.Start == 300 && r.End == 430);
    }

    [Fact]
    public async Task LiveBreaksStayProvisionalUntilAFullPassStoresItsResult()
    {
        await _store.PublishAsync(_item, new List<Break> { new(100, 160) }, "test", final: false);
        Assert.True(_workspace.IsProvisional(_item.Id));

        Assert.Equal(0, await _store.PublishAsync(_item, new List<Break> { new(100, 160) }, "test", final: true));
        Assert.False(_workspace.IsProvisional(_item.Id));
    }

    [Fact]
    public async Task ForgettingAnItemDropsOnlyItsComsegRows()
    {
        _segments.Add(_item.Id, Other, 100, 160);
        _segments.Add(_item.Id, Comseg, 300, 400);
        _workspace.MarkProvisional(_item.Id);

        await _store.ForgetAsync(_item);

        Assert.Equal(Other, Assert.Single(_segments.Rows).Provider);
        Assert.False(_workspace.IsProvisional(_item.Id));
    }

    private static string IdOf(string provider) => provider.ToLowerInvariant().GetMD5().ToString("N", CultureInfo.InvariantCulture);

    public sealed record Row(Guid Id, Guid ItemId, string Provider, double Start, double End);

    /// <summary>The segment table, filtering reads by provider the way Jellyfin does.</summary>
    private sealed class FakeSegments : IMediaSegmentManager
    {
        private readonly string[] _providers;

        public FakeSegments(params string[] providers) => _providers = providers;

        public List<Row> Rows { get; } = new();

        public Row Add(Guid itemId, string provider, double start, double end)
        {
            var row = new Row(Guid.NewGuid(), itemId, provider, start, end);
            Rows.Add(row);
            return row;
        }

        public Task<MediaSegmentDto> CreateSegmentAsync(MediaSegmentDto mediaSegment, string segmentProviderId)
        {
            var provider = _providers.Single(p => IdOf(p) == segmentProviderId);
            Rows.Add(new Row(mediaSegment.Id, mediaSegment.ItemId, provider, mediaSegment.StartTicks / (double)TimeSpan.TicksPerSecond, mediaSegment.EndTicks / (double)TimeSpan.TicksPerSecond));
            return Task.FromResult(mediaSegment);
        }

        public Task DeleteSegmentAsync(Guid segmentId)
        {
            Rows.RemoveAll(r => r.Id == segmentId);
            return Task.CompletedTask;
        }

        public Task<IEnumerable<MediaSegmentDto>> GetSegmentsAsync(BaseItem item, IEnumerable<MediaSegmentType>? typeFilter, LibraryOptions libraryOptions, bool filterByProvider = true)
        {
            IEnumerable<Row> rows = Rows.Where(r => r.ItemId == item.Id);
            if (filterByProvider)
            {
                var enabled = _providers.Where(p => !libraryOptions.DisabledMediaSegmentProviders.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                rows = rows.Where(r => enabled.Contains(r.Provider));
            }

            var dtos = rows.OrderBy(r => r.Start).Select(r => new MediaSegmentDto
            {
                Id = r.Id,
                ItemId = r.ItemId,
                Type = MediaSegmentType.Commercial,
                StartTicks = (long)Math.Round(r.Start * TimeSpan.TicksPerSecond),
                EndTicks = (long)Math.Round(r.End * TimeSpan.TicksPerSecond),
            }).ToList();
            return Task.FromResult<IEnumerable<MediaSegmentDto>>(dtos);
        }

        public IEnumerable<(string Name, string Id)> GetSupportedProviders(BaseItem item) => _providers.Select(p => (p, IdOf(p)));

        public Task RunSegmentPluginProviders(BaseItem baseItem, LibraryOptions libraryOptions, bool forceOverwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool IsTypeSupported(BaseItem baseItem) => true;

        public Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool HasSegments(Guid itemId) => Rows.Any(r => r.ItemId == itemId);
    }

    private sealed class FakePaths : IApplicationPaths
    {
        public FakePaths(string root) => DataPath = root;

        public string DataPath { get; }

        public string ProgramDataPath => DataPath;

        public string WebPath => DataPath;

        public string ProgramSystemPath => DataPath;

        public string ImageCachePath => DataPath;

        public string PluginsPath => DataPath;

        public string PluginConfigurationsPath => DataPath;

        public string LogDirectoryPath => DataPath;

        public string ConfigurationDirectoryPath => DataPath;

        public string SystemConfigurationFilePath => DataPath;

        public string CachePath => DataPath;

        public string TempDirectory => DataPath;

        public string VirtualDataPath => DataPath;

        public string TrickplayPath => DataPath;

        public string BackupPath => DataPath;

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }
}
