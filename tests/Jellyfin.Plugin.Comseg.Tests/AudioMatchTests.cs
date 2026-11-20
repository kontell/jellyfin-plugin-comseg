using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Comseg.Audio;
using Xunit;

namespace Jellyfin.Plugin.Comseg.Tests;

public class AudioMatchTests
{
    /// <summary>Random items stand in for unrelated audio: about half the bits differ.</summary>
    internal static uint[] Noise(int count, int seed)
    {
        var random = new Random(seed);
        var items = new uint[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = (uint)random.NextInt64(0, 1L << 32);
        }

        return items;
    }

    /// <summary>A copy with some bits flipped in every item, as a second airing of the same audio gives.</summary>
    internal static uint[] Blur(ReadOnlySpan<uint> items, int bits, int seed)
    {
        var random = new Random(seed);
        var copy = items.ToArray();
        for (var i = 0; i < copy.Length; i++)
        {
            for (var b = 0; b < bits; b++)
            {
                copy[i] ^= 1u << random.Next(32);
            }
        }

        return copy;
    }

    [Fact]
    public void TheSameAudioIsFoundWhereverItAirs()
    {
        var a = Noise(5000, 1);
        var b = Noise(3000, 2);

        // 30 s of a (242 items from 1000) airs again in b from 1700, slightly blurred.
        Blur(a.AsSpan(1000, 242), 2, 3).CopyTo(b, 1700);
        var runs = Matcher.Match(a, new FingerprintIndex(a), b);
        var run = Assert.Single(runs);
        Assert.InRange(run.AStart, 998, 1002);
        Assert.Equal(run.AStart - 1000, run.BStart - 1700);
        Assert.InRange(run.Length, 238, 246);
        Assert.InRange(run.BitError, 0.03, 0.10);
    }

    [Fact]
    public void UnrelatedOrTooShortOrTooBlurredAudioIsNot()
    {
        var a = Noise(4000, 4);
        var b = Noise(4000, 5);
        Assert.Empty(Matcher.Match(a, new FingerprintIndex(a), b));

        // Three seconds is under the four a run needs.
        a.AsSpan(500, 24).CopyTo(b.AsSpan(900));
        Assert.Empty(Matcher.Match(a, new FingerprintIndex(a), b));

        // Twelve bits in 32 differ: past the limit.
        Blur(a.AsSpan(2000, 300), 12, 6).CopyTo(b, 2500);
        Assert.Empty(Matcher.Match(a, new FingerprintIndex(a), b));
    }

    [Fact]
    public void SilenceSeedsNothing()
    {
        // A value that recurs everywhere, as silence does, is not indexed.
        var a = Noise(2000, 7);
        Array.Fill(a, 0x5555_5555u, 300, 400);
        var index = new FingerprintIndex(a);
        Assert.True(index.Find(0x5555_5555u).IsEmpty);
        Assert.Equal(1, index.Find(a[10]).Length);

        var b = Noise(2000, 8);
        Array.Fill(b, 0x5555_5555u, 1000, 400);
        Assert.Empty(Matcher.Match(a, index, b));
    }

    [Fact]
    public void ARecordingIsSearchedOnlyAgainstItsOwnEarlierPart()
    {
        var a = Noise(6000, 9);
        Blur(a.AsSpan(1000, 300), 2, 10).CopyTo(a, 4000);
        var index = new FingerprintIndex(a);

        // Itself, on the diagonal, is not a repeat; the airing 20 s or more away is.
        var runs = Matcher.Match(a, index, a, minDiagonal: Fingerprint.ItemAt(20));
        Assert.DoesNotContain(runs, r => r.Diagonal == 0);
        Assert.Contains(runs, r => r.AStart is >= 3998 and <= 4002 && r.BStart is >= 998 and <= 1002);

        var repeats = Matcher.Match(a, index, a.AsSpan(3900, 600), minDiagonal: int.MinValue);
        Assert.Contains(repeats, r => r.AStart is >= 998 and <= 1002 && r.BStart is >= 98 and <= 102);
    }

    [Fact]
    public void ItemsAndTimesConvertAtTheChromaprintHop()
    {
        Assert.Equal(0.12381, Fingerprint.Hop, 5);
        Assert.Equal(4846, Fingerprint.ItemAt(600));
        Assert.Equal(600.0, Fingerprint.TimeOf(4846), 0);
        Assert.Equal(0, Fingerprint.BitError([1u, 2u], [1u, 2u]));
        Assert.Equal(1, Fingerprint.BitError([0u], [uint.MaxValue]));

        var path = Path.GetTempFileName();
        try
        {
            var items = Noise(100, 11);
            Fingerprint.Save(path, items);
            Assert.Equal(400, new FileInfo(path).Length);
            Assert.Equal(items, Fingerprint.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
