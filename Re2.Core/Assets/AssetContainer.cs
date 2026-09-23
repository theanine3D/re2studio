using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Re2.Core.Assets;

/// <summary>One file in RE2's asset container.</summary>
public sealed record AssetFile(int Index, int Offset, int Size, uint Tag)
{
    /// <summary>Where this file's 8-byte trailer sits (the data is padded to an even offset first).</summary>
    public int TrailerOffset => (Offset + Size + 1) & ~1;

    /// <summary>Start of the next file in the container.</summary>
    public int NextOffset => TrailerOffset + AssetContainer.TrailerSize;

    /// <summary>Total ROM footprint including padding and trailer.</summary>
    public int StrideBytes => NextOffset - Offset;

    public override string ToString() => $"#{Index} @0x{Offset:X7} {Size:N0}B tag 0x{Tag:X8}";
}

/// <summary>RE2's asset container.</summary>
public static class AssetContainer
{
    public const int TrailerSize = 8;

    /// <summary>Upper bound on a single file, used to bound the trailer search.</summary>
    public const int DefaultMaxFileSize = 8 << 20;

    /// <summary>Smallest file the walker will accept.</summary>
    public const int DefaultMinFileSize = 8;

    private static uint U32(ReadOnlySpan<byte> rom, int offset) => BinaryPrimitives.ReadUInt32BigEndian(rom.Slice(offset, 4));

    /// <summary>Finds the trailer for a file starting at <paramref name="start"/>.</summary>
    public static bool TryReadFileAt(ReadOnlySpan<byte> rom, int start, out int size, out uint tag,
        int maxFileSize = DefaultMaxFileSize, int minFileSize = DefaultMinFileSize)
    {
        size = 0;
        tag = 0;
        if (start < 0 || start + TrailerSize > rom.Length) return false;

        int t = (start + minFileSize + 1) & ~1;         // trailers are always at an even offset
        int limit = Math.Min(start + maxFileSize, rom.Length - TrailerSize);

        for (; t <= limit; t += 2)
        {
            uint candidate = U32(rom, t + 4);
            int delta = t - start;
            if (candidate != (uint)delta && candidate != (uint)(delta - 1)) continue;

            size = (int)candidate;
            tag = U32(rom, t);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Walks the container forward from a known file start until the chain breaks or
    /// <paramref name="stop"/> is passed.
    /// </summary>
    public static List<AssetFile> Walk(ReadOnlySpan<byte> rom, int start, int stop,
        int maxFileSize = DefaultMaxFileSize)
    {
        var files = new List<AssetFile>();
        int cursor = start;

        while (cursor < stop)
        {
            if (!TryReadFileAt(rom, cursor, out int size, out uint tag, maxFileSize)) break;
            var file = new AssetFile(files.Count, cursor, size, tag);
            files.Add(file);
            cursor = file.NextOffset;
        }

        return files;
    }

    /// <summary>Steps one file backwards from a known file start.</summary>
    public static bool TryReadPreviousFile(ReadOnlySpan<byte> rom, int start, out int prevStart, out int size, out uint tag)
    {
        prevStart = 0;
        size = 0;
        tag = 0;

        int trailer = start - TrailerSize;
        if (trailer < 0 || (start & 1) != 0) return false;

        uint rawSize = U32(rom, trailer + 4);
        if (rawSize > (uint)trailer) return false;

        size = (int)rawSize;
        tag = U32(rom, trailer);

        int candidate = trailer - size;
        if ((candidate & 1) != 0) candidate--;          // an odd payload length leaves one pad byte
        if (candidate < 0) return false;

        // Confirm by walking forward: the trailer we started from must be the first one this
        // candidate resolves to.
        if (!TryReadFileAt(rom, candidate, out int checkSize, out _) || checkSize != size) return false;
        if (((candidate + size + 1) & ~1) != trailer) return false;

        prevStart = candidate;
        return true;
    }

    /// <summary>
    /// Walks backwards from a known file start to locate the first file of the container.
    /// </summary>
    public static int FindContainerStart(ReadOnlySpan<byte> rom, int knownFileStart, int floor = 0)
    {
        int cursor = knownFileStart;
        while (cursor > floor && TryReadPreviousFile(rom, cursor, out int prev, out _, out _))
            cursor = prev;
        return cursor;
    }

    /// <summary>
    /// Measures how many files chain from a candidate start, without materialising them.
    /// </summary>
    public static int ChainDepth(ReadOnlySpan<byte> rom, int start, int maxSteps, int maxFileSize = DefaultMaxFileSize)
    {
        int cursor = start;
        int steps = 0;
        while (steps < maxSteps)
        {
            if (!TryReadFileAt(rom, cursor, out int size, out _, maxFileSize)) break;
            cursor = ((cursor + size + 1) & ~1) + TrailerSize;
            steps++;
        }
        return steps;
    }

    /// <summary>
    /// Brute-forces the first file boundary in a range: a real boundary chains for many files, an
    /// arbitrary offset dies within one or two.
    /// </summary>
    public static int FindFirstFile(ReadOnlySpan<byte> rom, int searchStart, int searchEnd,
        int minChain = 8, int maxFileSize = 1 << 20)
    {
        for (int s = searchStart & ~1; s < searchEnd; s += 2)
            if (ChainDepth(rom, s, minChain, maxFileSize) >= minChain)
                return s;
        return -1;
    }
}
