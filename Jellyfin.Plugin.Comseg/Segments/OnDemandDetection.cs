using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Comseg.Comskip;
using Jellyfin.Plugin.Comseg.Live;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Segments;

/// <summary>
/// A full pass over a finished file, outside the scan: the on-demand Detect
/// endpoint and a finished catchup download. It replaces the item's rows.
/// </summary>
public sealed class OnDemandDetection
{
    private readonly CompletedPass _pass;
    private readonly SegmentStore _store;
    private readonly DetectionRegistry _registry;
    private readonly RecordingWatcher _watcher;
    private readonly ILogger<OnDemandDetection> _logger;

    /// <summary>Initializes a new instance of the <see cref="OnDemandDetection"/> class.</summary>
    /// <param name="pass">The full-pass runner.</param>
    /// <param name="store">The segment store.</param>
    /// <param name="registry">The detection registry.</param>
    /// <param name="watcher">The watcher, whose finished jobs stand down.</param>
    /// <param name="logger">The logger.</param>
    public OnDemandDetection(CompletedPass pass, SegmentStore store, DetectionRegistry registry, RecordingWatcher watcher, ILogger<OnDemandDetection> logger)
    {
        _pass = pass;
        _store = store;
        _registry = registry;
        _watcher = watcher;
        _logger = logger;
    }

    /// <summary>Starts a full pass over an item and stores its breaks.</summary>
    /// <param name="item">The item.</param>
    /// <param name="why">What asked for it, for the log.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The pass, which yields how many breaks it stored or null when it produced nothing; null when a pass over the item is already running.</returns>
    public Task<int?>? TryRun(BaseItem item, string why, CancellationToken cancellationToken)
    {
        if (!_registry.TryEnter(item.Id))
        {
            return null;
        }

        // A finished live job may still be keeping its breaks in place; this
        // pass replaces them.
        _watcher.StandDown(item.Id);
        var config = ComsegPlugin.Config;
        return Task.Run(
            async () =>
            {
                try
                {
                    var breaks = await _pass.RunAsync(item.Path, config, cancellationToken).ConfigureAwait(false);
                    if (breaks is null)
                    {
                        return (int?)null;
                    }

                    if (await _store.PublishAsync(item, breaks, why, final: true).ConfigureAwait(false) == 0)
                    {
                        _logger.LogInformation("Comseg: the {Why} pass over {Name} agrees with the stored breaks", why, item.Name);
                    }

                    return breaks.Count;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Comseg: {Why} detection failed for {Name}", why, item.Name);
                    return null;
                }
                finally
                {
                    _registry.Exit(item.Id);
                }
            },
            CancellationToken.None);
    }
}
