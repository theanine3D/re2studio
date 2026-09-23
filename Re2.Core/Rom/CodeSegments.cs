using System;
using System.Collections.Generic;
using Re2.Core.Codecs;

namespace Re2.Core.Rom;

/// <summary>One raw-deflate stream in the ROM.</summary>
public sealed record CodeSegment(int Index, int RomOffset, int CompressedSize, int DecompressedSize)
{
    public int RomEnd => RomOffset + CompressedSize;
    public double Ratio => CompressedSize == 0 ? 0 : (double)DecompressedSize / CompressedSize;
}

/// <summary>Locates the chain of raw-deflate streams that holds RE2's code and data overlays.</summary>
public static class CodeSegments
{
    /// <summary>Maximum alignment padding tolerated between the end of one stream and the start of the next.</summary>
    public const int MaxPadding = 64;

    /// <summary>Ignore candidate streams shorter than this; guards against tiny false positives.</summary>
    private const int MinDecompressedSize = 64;

    /// <summary>
    /// Walks the deflate chain from <paramref name="start"/> until a stream fails to decode or
    /// <paramref name="limit"/> is reached.
    /// </summary>
    public static List<CodeSegment> Scan(ReadOnlySpan<byte> rom, int start, int limit)
    {
        var segments = new List<CodeSegment>();
        int cursor = start;

        while (cursor < limit)
        {
            if (!TryFindStreamAt(rom, cursor, limit, out int offset, out int compressed, out int decompressed))
                break;

            segments.Add(new CodeSegment(segments.Count, offset, compressed, decompressed));
            cursor = offset + compressed;
        }

        return segments;
    }

    private static bool TryFindStreamAt(ReadOnlySpan<byte> rom, int cursor, int limit,
        out int offset, out int compressed, out int decompressed)
    {
        for (int pad = 0; pad < MaxPadding; pad++)
        {
            int candidate = cursor + pad;
            if (candidate >= limit) break;

            if (Inflate.TryInflateRaw(rom[candidate..], out var data, out int consumed)
                && data.Length >= MinDecompressedSize)
            {
                offset = candidate;
                compressed = consumed;
                decompressed = data.Length;
                return true;
            }
        }

        offset = compressed = decompressed = 0;
        return false;
    }

    /// <summary>Inflates a single segment.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> rom, CodeSegment segment)
        => RawDeflate.Decompress(rom.Slice(segment.RomOffset, segment.CompressedSize), out _);

    /// <summary>Inflates every segment and concatenates the results, in ROM order.</summary>
    public static byte[] DecompressAll(ReadOnlySpan<byte> rom, IReadOnlyList<CodeSegment> segments)
    {
        int total = 0;
        foreach (var s in segments) total += s.DecompressedSize;

        var output = new byte[total];
        int pos = 0;
        foreach (var s in segments)
        {
            var data = Decompress(rom, s);
            data.CopyTo(output, pos);
            pos += data.Length;
        }
        return output;
    }
}
