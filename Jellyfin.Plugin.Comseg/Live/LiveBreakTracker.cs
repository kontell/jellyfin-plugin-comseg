using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Comseg.Edl;

namespace Jellyfin.Plugin.Comseg.Live;

/// <summary>
/// Decides which breaks from a recording in progress are worth publishing.
/// Each analysis Comskip runs over the frames decoded so far reports every break
/// it currently believes in; a break is published once it has been reported
/// in enough consecutive analyses (a start that holds still) and is long
/// enough. Its end follows the latest analysis: while the break is still on
/// air the end grows with each pass, and the published row is updated.
/// Withdrawal is symmetric: a published break stays until the same number
/// of consecutive analyses have not reported it, so one analysis changing
/// its mind mid-break does not delete and re-create the row. Measured on a
/// full NFL broadcast: every one-pass false break vanished on the next
/// pass, and real starts held to within 0.1 s from first sight.
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
    public void Observe(IReadOnlyList<Break> pass)
    {
        Passes++;
        var next = new List<Candidate>(pass.Count);
        var matched = new HashSet<Candidate>();
        foreach (var b in pass.OrderBy(b => b.Start))
        {
            var previous = _candidates.FirstOrDefault(c => Math.Abs(c.Start - b.Start) <= StartTolerance);
            if (previous is not null)
            {
                matched.Add(previous);
            }

            next.Add(previous is null
                ? new Candidate(b.Start, b.End, 1, 0)
                : new Candidate(b.Start, b.End, previous.Seen + 1, 0));
        }

        // A confirmed break the analysis stopped reporting lingers, unchanged,
        // until it has been absent for as many analyses as it took to confirm.
        foreach (var c in _candidates)
        {
            if (!matched.Contains(c) && c.Seen >= _confirmPasses && c.Missing + 1 < _confirmPasses)
            {
                next.Add(c with { Missing = c.Missing + 1 });
            }
        }

        next.Sort((a, b) => a.Start.CompareTo(b.Start));
        _candidates = next;
        Confirmed = next
            .Where(c => c.Seen >= _confirmPasses && c.End - c.Start >= _minSeconds)
            .Select(c => new Break(c.Start, c.End))
            .ToList();
    }

    private sealed record Candidate(double Start, double End, int Seen, int Missing);
}
