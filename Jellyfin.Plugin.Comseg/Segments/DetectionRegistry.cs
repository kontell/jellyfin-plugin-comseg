using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Comseg.Segments;

/// <summary>
/// Items a live job or a finalisation currently owns, so the scheduled scan's
/// provider does not start a second pass on the same recording.
/// </summary>
public sealed class DetectionRegistry
{
    private readonly ConcurrentDictionary<Guid, DateTime> _busy = new();

    /// <summary>Claims an item.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>False when already claimed.</returns>
    public bool TryEnter(Guid itemId) => _busy.TryAdd(itemId, DateTime.UtcNow);

    /// <summary>Releases an item.</summary>
    /// <param name="itemId">The item.</param>
    public void Exit(Guid itemId) => _busy.TryRemove(itemId, out _);

    /// <summary>Whether an item is claimed.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True when claimed.</returns>
    public bool Contains(Guid itemId) => _busy.ContainsKey(itemId);
}
