using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Comseg.Edl;
using Jellyfin.Plugin.Comseg.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comseg.Segments;

/// <summary>
/// The plugin's Commercial rows in Jellyfin's segment store, by item. Rows
/// are replaced rather than edited (the store has create and delete only),
/// so a break whose end moved is a delete and a create; clients must
/// re-fetch by item, never cache by segment id. The segment DTO carries no
/// provider id, so on first contact with an item every Commercial row it
/// already has is adopted as ours.
/// </summary>
public sealed class SegmentStore
{
    private const double StartTolerance = 1.5;
    private const double EndTolerance = 0.5;

    private readonly IMediaSegmentManager _segments;
    private readonly ILibraryManager _library;
    private readonly ILogger<SegmentStore> _logger;
    private readonly Dictionary<Guid, List<Row>> _rows = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="SegmentStore"/> class.</summary>
    /// <param name="segments">The segment manager.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public SegmentStore(IMediaSegmentManager segments, ILibraryManager library, ILogger<SegmentStore> logger)
    {
        _segments = segments;
        _library = library;
        _logger = logger;
    }

    /// <summary>The rows known for an item (empty until adopted).</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The rows.</returns>
    public IReadOnlyList<Row> Known(Guid itemId)
    {
        lock (_rows)
        {
            return _rows.TryGetValue(itemId, out var rows) ? rows.ToList() : Array.Empty<Row>();
        }
    }

    /// <summary>Brings an item's existing Commercial rows under management.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The rows.</returns>
    public async Task<IReadOnlyList<Row>> AdoptAsync(BaseItem item)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await AdoptLockedAsync(item).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Makes the stored rows match the given breaks, touching only what changed.</summary>
    /// <param name="item">The item.</param>
    /// <param name="breaks">The breaks to publish.</param>
    /// <returns>How many rows were created or deleted.</returns>
    public async Task<int> PublishAsync(BaseItem item, IReadOnlyList<Break> breaks)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var known = (await AdoptLockedAsync(item).ConfigureAwait(false)).ToList();
            var keep = new List<Row>();
            var create = new List<Break>();
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
                    create.Add(b);
                }
                else
                {
                    keep.Add(row);
                }
            }

            // Rows no current break accounts for: a confirmed break the
            // analysis later withdrew.
            foreach (var orphan in known)
            {
                await DeleteAsync(orphan).ConfigureAwait(false);
            }

            foreach (var b in create)
            {
                keep.Add(await CreateAsync(item.Id, b).ConfigureAwait(false));
            }

            keep.Sort((a, b) => a.Start.CompareTo(b.Start));
            lock (_rows)
            {
                _rows[item.Id] = keep;
            }

            var changes = known.Count + create.Count;
            if (changes > 0)
            {
                _logger.LogInformation("Comseg published {Count} commercial breaks for {Name} ({Changes} rows changed)", keep.Count, item.Name, changes);
            }

            return changes;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces every row for an item with the given breaks.</summary>
    /// <param name="item">The item.</param>
    /// <param name="breaks">The final breaks.</param>
    /// <returns>A task.</returns>
    public async Task ReplaceAsync(BaseItem item, IReadOnlyList<Break> breaks)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var known = await AdoptLockedAsync(item).ConfigureAwait(false);
            foreach (var row in known)
            {
                await DeleteAsync(row).ConfigureAwait(false);
            }

            var rows = new List<Row>();
            foreach (var b in breaks.OrderBy(b => b.Start))
            {
                rows.Add(await CreateAsync(item.Id, b).ConfigureAwait(false));
            }

            lock (_rows)
            {
                _rows[item.Id] = rows;
            }

            _logger.LogInformation("Comseg stored {Count} commercial breaks for {Name} (final pass)", rows.Count, item.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the in-memory knowledge of an item (the rows stay in Jellyfin).</summary>
    /// <param name="itemId">The item.</param>
    public void Forget(Guid itemId)
    {
        lock (_rows)
        {
            _rows.Remove(itemId);
        }
    }

    private static long Ticks(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    private async Task<IReadOnlyList<Row>> AdoptLockedAsync(BaseItem item)
    {
        lock (_rows)
        {
            if (_rows.TryGetValue(item.Id, out var rows))
            {
                return rows.ToList();
            }
        }

        var options = _library.GetLibraryOptions(item);
        var existing = await _segments
            .GetSegmentsAsync(item, new[] { MediaSegmentType.Commercial }, options, filterByProvider: false)
            .ConfigureAwait(false);
        var adopted = existing
            .Select(d => new Row(d.Id, d.StartTicks / (double)TimeSpan.TicksPerSecond, d.EndTicks / (double)TimeSpan.TicksPerSecond))
            .OrderBy(r => r.Start)
            .ToList();
        lock (_rows)
        {
            _rows[item.Id] = adopted;
        }

        if (adopted.Count > 0)
        {
            _logger.LogDebug("Comseg adopted {Count} existing commercial rows for {Name}", adopted.Count, item.Name);
        }

        return adopted.ToList();
    }

    private async Task<Row> CreateAsync(Guid itemId, Break b)
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
        return new Row(dto.Id, b.Start, b.End);
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

    /// <summary>A stored row.</summary>
    /// <param name="Id">The segment id.</param>
    /// <param name="Start">Start, seconds.</param>
    /// <param name="End">End, seconds.</param>
    public sealed record Row(Guid Id, double Start, double End);
}
