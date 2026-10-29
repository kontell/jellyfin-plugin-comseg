using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Segments;

/// <summary>
/// Comseg's Commercial rows in Jellyfin's segment store, by item. Every
/// store reads the rows Comseg has on the item right now and makes them
/// match the breaks given, touching only what differs: Jellyfin deletes an
/// item's segments whenever a refresh notices its file changed, which a
/// recording in progress is by definition, so remembering what was written
/// would only remember rows that are gone. Only rows under Comseg's own
/// provider id are read, kept or deleted; a row another provider wrote, or
/// one an admin added under another id, is never touched. Rows are replaced
/// rather than edited (the store has create and delete only), so a break
/// whose end moved is a delete and a create; clients must re-fetch by item,
/// never cache by segment id.
/// </summary>
public sealed class SegmentStore
{
    private const double StartTolerance = 1.5;
    private const double EndTolerance = 0.5;

    private readonly IMediaSegmentManager _segments;
    private readonly Workspace _workspace;
    private readonly ILogger<SegmentStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="SegmentStore"/> class.</summary>
    /// <param name="segments">The segment manager.</param>
    /// <param name="workspace">The workspace, for the provisional marks.</param>
    /// <param name="logger">The logger.</param>
    public SegmentStore(IMediaSegmentManager segments, Workspace workspace, ILogger<SegmentStore> logger)
    {
        _segments = segments;
        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>Makes the item's Comseg rows match the given breaks, touching only what differs.</summary>
    /// <param name="item">The item.</param>
    /// <param name="breaks">The breaks to store.</param>
    /// <param name="why">A word for the log: what these breaks are.</param>
    /// <param name="final">
    /// Whether the breaks are a full pass's result. Live breaks mark the item
    /// provisional before its rows are written; a full pass's result clears
    /// the mark once they are.
    /// </param>
    /// <returns>How many rows were created or deleted.</returns>
    public async Task<int> PublishAsync(BaseItem item, IReadOnlyList<Break> breaks, string why, bool final)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!final)
            {
                MarkProvisional(item);
            }

            var known = await ReadAsync(item).ConfigureAwait(false);
            var create = new List<Break>();
            var deleted = 0;
            foreach (var b in breaks)
            {
                var row = known.FirstOrDefault(r => Math.Abs(r.Start - b.Start) <= StartTolerance);
                if (row is null)
                {
                    create.Add(b);
                    continue;
                }

                known.Remove(row);
                if (Math.Abs(row.End - b.End) > EndTolerance || Math.Abs(row.Start - b.Start) > EndTolerance)
                {
                    await DeleteAsync(row).ConfigureAwait(false);
                    deleted++;
                    create.Add(b);
                }
            }

            // Rows no current break accounts for: a break the analysis withdrew.
            foreach (var orphan in known)
            {
                await DeleteAsync(orphan).ConfigureAwait(false);
                deleted++;
            }

            foreach (var b in create)
            {
                await CreateAsync(item.Id, b).ConfigureAwait(false);
            }

            if (final)
            {
                _workspace.ClearProvisional(item.Id);
            }

            var changes = deleted + create.Count;
            if (changes > 0)
            {
                _logger.LogInformation("Comseg stored {Count} commercial breaks for {Name} ({Why}; {Changes} rows changed)", breaks.Count, item.Name, why, changes);
            }

            return changes;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes an item's Comseg rows and its provisional mark: Jellyfin removed the item.</summary>
    /// <param name="item">The removed item.</param>
    /// <returns>How many rows were deleted.</returns>
    public Task<int> ForgetAsync(BaseItem item) => PublishAsync(item, Array.Empty<Break>(), "item removed", final: true);

    private static long Ticks(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    private void MarkProvisional(BaseItem item)
    {
        try
        {
            _workspace.MarkProvisional(item.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the mark, a final pass that never stores leaves these rows
            // as the item's result; still better than storing none.
            _logger.LogWarning(ex, "Comseg could not mark the live breaks for {Name} as provisional", item.Name);
        }
    }

    private async Task<List<Row>> ReadAsync(BaseItem item)
    {
        // Jellyfin filters a segment read by provider only through library
        // options, so name every other provider as disabled: what comes back is
        // Comseg's rows alone, whatever the item's own library enables.
        var providers = _segments.GetSupportedProviders(item).Select(p => p.Name).ToList();
        if (!providers.Contains(ComsegSegmentProvider.ProviderName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(item.Name + " does not take media segments");
        }

        var options = new LibraryOptions
        {
            DisabledMediaSegmentProviders = providers
                .Where(n => !string.Equals(n, ComsegSegmentProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };
        var existing = await _segments
            .GetSegmentsAsync(item, new[] { MediaSegmentType.Commercial }, options, filterByProvider: true)
            .ConfigureAwait(false);
        return existing
            .Select(d => new Row(d.Id, d.StartTicks / (double)TimeSpan.TicksPerSecond, d.EndTicks / (double)TimeSpan.TicksPerSecond))
            .OrderBy(r => r.Start)
            .ToList();
    }

    private async Task CreateAsync(Guid itemId, Break b)
    {
        var dto = new MediaSegmentDto
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            Type = MediaSegmentType.Commercial,
            StartTicks = Ticks(b.Start),
            EndTicks = Ticks(b.End),
        };
        await _segments.CreateSegmentAsync(dto, ComsegSegmentProvider.ProviderId).ConfigureAwait(false);
    }

    private async Task DeleteAsync(Row row)
    {
        try
        {
            await _segments.DeleteSegmentAsync(row.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Comseg could not delete segment {Id}", row.Id);
        }
    }

    private sealed record Row(Guid Id, double Start, double End);
}
