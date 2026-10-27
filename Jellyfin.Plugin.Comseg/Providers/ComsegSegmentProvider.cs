using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Comseg.Comskip;
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
/// recordings that have no rows yet, and it runs a full pass then. Rows
/// written during a recording (by the watcher) come back as
/// ExistingSegments and are returned unchanged.
///
/// Jellyfin constructs every provider while building IMediaSegmentManager,
/// which IProviderManager needs, which the library and recordings managers
/// need: anything from that family taken in this constructor is a cycle that
/// stops the server. So the library manager is resolved per call.
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
    private readonly ILogger<ComsegSegmentProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="ComsegSegmentProvider"/> class.</summary>
    /// <param name="services">The service provider, for the library manager.</param>
    /// <param name="paths">The recording path rules.</param>
    /// <param name="pass">The full-pass runner.</param>
    /// <param name="registry">The registry of items a live job owns.</param>
    /// <param name="logger">The logger.</param>
    public ComsegSegmentProvider(
        IServiceProvider services,
        RecordingPaths paths,
        CompletedPass pass,
        DetectionRegistry registry,
        ILogger<ComsegSegmentProvider> logger)
    {
        _services = services;
        _paths = paths;
        _pass = pass;
        _registry = registry;
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
        if (request.ExistingSegments.Count > 0)
        {
            return request.ExistingSegments.ToList();
        }

        var config = ComsegPlugin.Config;
        if (!config.EnableCompletedDetection)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var item = _services.GetRequiredService<ILibraryManager>().GetItemById(request.ItemId);
        if (item is null || !await Supports(item).ConfigureAwait(false))
        {
            return Array.Empty<MediaSegmentDto>();
        }

        if (!_registry.TryEnter(item.Id))
        {
            _logger.LogDebug("Comseg: {Name} is being handled by a live job; the scan leaves it", item.Name);
            return Array.Empty<MediaSegmentDto>();
        }

        try
        {
            var breaks = await _pass.RunAsync(item.Path, config, cancellationToken).ConfigureAwait(false);
            if (breaks is null)
            {
                return Array.Empty<MediaSegmentDto>();
            }

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
    /// Jellyfin 12 asks providers to drop per-item data when an item's
    /// segments are deleted. Comseg keeps none after a pass finishes. (Not an
    /// interface member on 10.11, where it is simply an unused method.)
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A completed task.</returns>
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;
}
