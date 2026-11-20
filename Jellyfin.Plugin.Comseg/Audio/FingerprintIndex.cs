using System;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>
/// Where each item value occurs in a fingerprint, for finding the places two
/// fingerprints might share audio. Values that occur more than a few times
/// are silence or steady tones, which match everywhere and prove nothing, so
/// they are left out.
/// </summary>
public sealed class FingerprintIndex
{
    /// <summary>A value that occurs more often than this is not indexed.</summary>
    public const int MaxValueCount = 12;

    private readonly uint[] _values;
    private readonly int[] _positions;

    /// <summary>Initializes a new instance of the <see cref="FingerprintIndex"/> class.</summary>
    /// <param name="items">The fingerprint.</param>
    /// <param name="length">How many of its first items to index; all of them when negative.</param>
    public FingerprintIndex(ReadOnlySpan<uint> items, int length = -1)
    {
        var n = length < 0 ? items.Length : Math.Min(length, items.Length);
        var values = items[..n].ToArray();
        var positions = new int[n];
        for (var i = 0; i < n; i++)
        {
            positions[i] = i;
        }

        // Sort by value; positions follow, ascending within a value.
        Array.Sort(values, positions);
        var keep = 0;
        for (var start = 0; start < n;)
        {
            var end = start + 1;
            while (end < n && values[end] == values[start])
            {
                end++;
            }

            if (end - start <= MaxValueCount)
            {
                Array.Sort(positions, start, end - start);
                for (var k = start; k < end; k++)
                {
                    values[keep] = values[k];
                    positions[keep] = positions[k];
                    keep++;
                }
            }

            start = end;
        }

        _values = values[..keep];
        _positions = positions[..keep];
        Length = n;
    }

    /// <summary>Gets how many items were indexed.</summary>
    public int Length { get; }

    /// <summary>The positions at which a value occurs, ascending.</summary>
    /// <param name="value">The item value.</param>
    /// <returns>The positions; empty when the value is absent or too common.</returns>
    public ReadOnlySpan<int> Find(uint value)
    {
        var at = Array.BinarySearch(_values, value);
        if (at < 0)
        {
            return ReadOnlySpan<int>.Empty;
        }

        var start = at;
        while (start > 0 && _values[start - 1] == value)
        {
            start--;
        }

        var end = at + 1;
        while (end < _values.Length && _values[end] == value)
        {
            end++;
        }

        return _positions.AsSpan(start, end - start);
    }
}
