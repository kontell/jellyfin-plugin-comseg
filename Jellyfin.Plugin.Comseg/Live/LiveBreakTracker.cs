using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Live;

/// <summary>
/// Decides which breaks from a recording in progress are worth publishing.
/// Each full analysis Comskip runs over the growing file reports every break
/// it currently believes in; a break is published once it has been reported
/// in enough consecutive analyses (a start that holds still) and is long
/// enough. Its end follows the latest analysis: while the break is still on
/// air the end grows with each pass, and the published row is updated.
/// Measured on a full NFL broadcast: every one-pass false break vanished on
/// the next pass, and real starts held to within 0.1 s from first sight.
/// </summary>
public sealed class LiveBreakTracker
{
    private const double StartTolerance = 1.5;

    private readonly int _confirmPasses;
    private readonly double _minSeconds;
    private List<Candidate> _candidates = new();

    /// <summary>Initializes a new instance of the <see cref="LiveBreakTracker"/> class.</summary>
    /// <param name="confirmPasses">Consecutive analyses a break must appear in.</param>
    /// <param name="minSeconds">Shortest break published.</param>
    public LiveBreakTracker(int confirmPasses, double minSeconds)
    {
        _confirmPasses = Math.Max(1, confirmPasses);
        _minSeconds = Math.Max(0, minSeconds);
    }

    /// <summary>Gets the breaks that are currently publishable, sorted by start.</summary>
    public IReadOnlyList<Break> Confirmed { get; private set; } = Array.Empty<Break>();

    /// <summary>Gets how many analyses have been observed.</summary>
    public int Passes { get; private set; }

    /// <summary>Feeds one analysis.</summary>
    /// <param name="pass">The breaks that analysis reported.</param>
    /// <returns>True when the publishable list changed.</returns>
    public bool Observe(IReadOnlyList<Break> pass)
    {
        Passes++;
        var next = new List<Candidate>(pass.Count);
        foreach (var b in pass.OrderBy(b => b.Start))
        {
            var previous = _candidates.FirstOrDefault(c => Math.Abs(c.Start - b.Start) <= StartTolerance);
            next.Add(previous is null
                ? new Candidate(b.Start, b.End, 1)
                : new Candidate(b.Start, b.End, previous.Seen + 1));
        }

        _candidates = next;
        var confirmed = next
            .Where(c => c.Seen >= _confirmPasses && c.End - c.Start >= _minSeconds)
            .Select(c => new Break(c.Start, c.End))
            .ToList();
        var changed = !SameList(confirmed, Confirmed);
        Confirmed = confirmed;
        return changed;
    }

    private static bool SameList(IReadOnlyList<Break> a, IReadOnlyList<Break> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Matches(b[i], 0.01))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record Candidate(double Start, double End, int Seen);
}
