using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Comseg.Audio;

/// <summary>
/// Chromaprint fingerprints as ffmpeg's chromaprint muxer writes them raw:
/// one 32-bit item per 1365 samples of 11,025 Hz mono, so an item every
/// 0.1238 s, each describing about 2.7 s of audio. A slice taken from time T
/// lines up with the whole file's fingerprint at item round(T / hop).
/// </summary>
public static class Fingerprint
{
    /// <summary>Seconds per item.</summary>
    public const double Hop = 1365.0 / 11025.0;

    /// <summary>The item at a time.</summary>
    /// <param name="seconds">Seconds from the start of the recording.</param>
    /// <returns>The item index.</returns>
    public static int ItemAt(double seconds) => (int)Math.Round(seconds / Hop);

    /// <summary>The time of an item.</summary>
    /// <param name="item">The item index.</param>
    /// <returns>Seconds from the start of the recording.</returns>
    public static double TimeOf(int item) => item * Hop;

    /// <summary>The share of differing bits between two runs of items: 0 for the same audio, about 0.5 for unrelated audio.</summary>
    /// <param name="a">One run.</param>
    /// <param name="b">The other, as long.</param>
    /// <returns>The bit error rate.</returns>
    public static double BitError(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 1;
        }

        long bits = 0;
        for (var i = 0; i < a.Length; i++)
        {
            bits += BitOperations.PopCount(a[i] ^ b[i]);
        }

        return bits / (32.0 * a.Length);
    }

    /// <summary>Reads a raw fingerprint: little-endian 32-bit items, which every platform Jellyfin runs on uses natively.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>The items.</returns>
    public static uint[] FromBytes(ReadOnlySpan<byte> bytes)
        => MemoryMarshal.Cast<byte, uint>(bytes[..(bytes.Length / 4 * 4)]).ToArray();

    /// <summary>Reads a raw fingerprint file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The items.</returns>
    public static uint[] Load(string path) => FromBytes(File.ReadAllBytes(path));

    /// <summary>Writes a raw fingerprint file, as ffmpeg would.</summary>
    /// <param name="path">The file.</param>
    /// <param name="items">The items.</param>
    public static void Save(string path, ReadOnlySpan<uint> items) => File.WriteAllBytes(path, MemoryMarshal.AsBytes(items).ToArray());
}
