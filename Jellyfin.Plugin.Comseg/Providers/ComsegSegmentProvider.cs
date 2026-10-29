using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Live;
using Jellyfin.Plugin.Comseg.Segments;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Providers;

/// <summary>
/// The media segment provider. Registering one is what makes the plugin's
/// rows visible: Jellyfin filters reads to registered, per-library enabled
/// providers. The scheduled Media Segment Scan calls it for finished
/// recordings, and it runs a full pass on those that have no rows yet. Rows
/// the watcher stored come back as ExistingSegments and are returned
/// unchanged, unless they are provisional (live analysis only, because the
/// final pass never stored): then the full pass runs after all.
///
/// Jellyfin constructs every provider while building IMediaSegmentManager,
/// which IProviderManager needs, which the library and recordings managers
/// need: anything from that family taken in this constructor is a cycle that
/// stops the server. So the library manager and the watcher, which takes
/// them, are resolved per call.
/// </summary>
public sealed class ComsegSegmentProvider : IMediaSegmentProvider
{
    /// <summary>The provider name, which Jellyfin hashes into the provider id.</summary>
    public const string ProviderName = "Comseg";

    /// <summary>The provider id as Jellyfin derives it: the MD5 of the lower-cased name.</summary>
    public static readonly string ProviderId = ProviderName.ToLowerInvariant().GetMD5().ToString("N", CultureInfo.InvariantCulture);

    private readonly IServiceProvider _services;
    private readonly RecordingPaths _paths;
    private readonly CompletedPass _pass;
    private readonly DetectionRegistry _registry;
    private readonly Workspace _workspace;
    private readonly ILogger<ComsegSegmentProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="ComsegSegmentProvider"/> class.</summary>
    /// <param name="services">The service provider, for the library manager and the watcher.</param>
    /// <param name="paths">The recording path rules.</param>
    /// <param name="pass">The full-pass runner.</param>
    /// <param name="registry">The registry of items a live job owns.</param>
    /// <param name="workspace">The workspace, for the provisional marks.</param>
    /// <param name="logger">The logger.</param>
    public ComsegSegmentProvider(
        IServiceProvider services,
        RecordingPaths paths,
        CompletedPass pass,
        DetectionRegistry registry,
        Workspace workspace,
        ILogger<ComsegSegmentProvider> logger)
    {
        _services = services;
        _paths = paths;
        _pass = pass;
        _registry = registry;
        _workspace = workspace;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        var config = ComsegPlugin.Config;
        var ok = item is Video
            && item.IsFileProtocol
            && !string.IsNullOrEmpty(item.Path)
            && _paths.IsRecording(item.Path, config)
            && !_paths.IsActiveRecording(item.Path);
        return ValueTask.FromResult(ok);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        // Jellyfin replaces the item's Comseg rows with whatever comes back
        // unless it matches them, so every way out without a new result hands
        // the existing rows back.
        var existing = request.ExistingSegments.ToList();
        if (existing.Count > 0 && !_workspace.IsProvisional(request.ItemId))
        {
            return existing;
        }

        var config = ComsegPlugin.Config;
        if (!config.EnableCompletedDetection)
        {
            return existing;
        }

        var item = _services.GetRequiredService<ILibraryManager>().GetItemById(request.ItemId);
        if (item is null || !await Supports(item).ConfigureAwait(false))
        {
            return existing;
        }

        if (!_registry.TryEnter(item.Id))
        {
            _logger.LogDebug("Comseg: {Name} is being handled by a live job; the scan leaves it", item.Name);
            return existing;
        }

        try
        {
            // A finished live job may still be keeping its breaks in place; this
            // pass replaces them.
            _services.GetService<RecordingWatcher>()?.StandDown(item.Id);
            var breaks = await _pass.RunAsync(item.Path, config, cancellationToken).ConfigureAwait(false);
            if (breaks is null)
            {
                return existing;
            }

            _workspace.ClearProvisional(item.Id);
            return breaks.Select(b => new MediaSegmentDto
            {
                ItemId = request.ItemId,
                Type = MediaSegmentType.Commercial,
                StartTicks = (long)Math.Round(b.Start * TimeSpan.TicksPerSecond),
                EndTicks = (long)Math.Round(b.End * TimeSpan.TicksPerSecond),
            }).ToList();
        }
        finally
        {
            _registry.Exit(item.Id);
        }
    }

    /// <summary>
    /// Jellyfin calls this just before it deletes an item's segments, which a
    /// refresh does whenever it notices the file changed: during a recording,
    /// and at least once when the file closes. The watcher's job for the item,
    /// if there is one, puts its breaks back straight after. Comseg keeps no
    /// per-item data that a prune should drop; a provisional mark stays, since
    /// the rows it describes come back.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        _services.GetService<RecordingWatcher>()?.OnPruning(itemId);
        return Task.CompletedTask;
    }
}
