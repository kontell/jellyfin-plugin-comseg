using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Eval;

/// <summary>Small helpers over lists of time spans.</summary>
public static class Spans
{
    /// <summary>Whether any span holds a moment (start included, end not).</summary>
    /// <param name="spans">The spans.</param>
    /// <param name="t">The moment.</param>
    /// <returns>True when one does.</returns>
    public static bool Contains(IReadOnlyList<Break> spans, double t)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            if (t >= spans[i].Start && t < spans[i].End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How many seconds two spans share.</summary>
    /// <param name="a">One span.</param>
    /// <param name="b">The other.</param>
    /// <returns>The overlap, zero when apart.</returns>
    public static double Overlap(Break a, Break b) => Math.Max(0, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start));
}
