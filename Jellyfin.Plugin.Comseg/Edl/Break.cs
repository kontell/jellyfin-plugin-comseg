using System;

namespace Jellyfin.Plugin.Comseg.Edl;

/// <summary>A commercial break, in seconds from the start of the recording.</summary>
/// <param name="Start">Start, seconds.</param>
/// <param name="End">End, seconds.</param>
public readonly record struct Break(double Start, double End)
{
    /// <summary>Gets the length in seconds.</summary>
    public double Length => End - Start;

    /// <summary>Whether two breaks are the same to within a tolerance.</summary>
    /// <param name="other">The other break.</param>
    /// <param name="tolerance">Seconds.</param>
    /// <returns>True when both ends agree within the tolerance.</returns>
    public bool Matches(Break other, double tolerance)
        => Math.Abs(Start - other.Start) <= tolerance && Math.Abs(End - other.End) <= tolerance;
}
