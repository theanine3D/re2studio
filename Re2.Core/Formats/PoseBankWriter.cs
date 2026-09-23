using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Re2.Core.Assets;
using Re2.Core.Rom;

namespace Re2.Core.Formats;

/// <summary>
/// Writes poses back into a pose bank, and splits the assembled bank into the chunk assets it came from
/// -- the write side of <see cref="PoseBank"/>.
/// </summary>
public static class PoseBankWriter
{
    /// <summary>Angles are 12 bits, so this is the largest value a joint angle can hold.</summary>
    public const int MaxAngle = PoseBank.AngleUnitsPerTurn - 1;

    /// <summary>
    /// Writes the rest pose -- each joint's offset from its parent -- back into an assembled bank.
    /// </summary>
    public static int WriteRestOffsets(PoseBank bank, IReadOnlyList<(int X, int Y, int Z)> offsets)
    {
        if (offsets.Count < bank.PartCount)
            throw new InvalidDataException(
                $"The skeleton has {bank.PartCount} joints but only {offsets.Count} offsets were given.");

        int end = PoseBank.RestOffsetsOffset + bank.PartCount * 3 * 2;
        if (end > bank.Data.Length)
            throw new InvalidDataException("The rest offsets do not fit inside this bank.");

        int clamped = 0;

        short Fit(int value)
        {
            if (value > short.MaxValue) { clamped++; return short.MaxValue; }
            if (value < short.MinValue) { clamped++; return short.MinValue; }
            return (short)value;
        }

        // Halfword pairs are stored swapped, hence the ^1 -- the same indexing the reader uses.
        void Write(int halfwordIndex, short value)
            => BinaryPrimitives.WriteInt16BigEndian(
                bank.Data.AsSpan(PoseBank.RestOffsetsOffset + (halfwordIndex ^ 1) * 2, 2), value);

        for (int part = 0; part < bank.PartCount; part++)
        {
            var (x, y, z) = offsets[part];
            Write(part * 3, Fit(x));
            Write(part * 3 + 1, Fit(y));
            Write(part * 3 + 2, Fit(z));
        }

        return clamped;
    }

    public static void WritePose(PoseBank bank, int index, (short X, short Y, short Z) rootTranslation,
                                 IReadOnlyList<(int X, int Y, int Z)> angles)
    {
        if (index < 0 || index >= bank.PoseCount)
            throw new ArgumentOutOfRangeException(nameof(index), $"Pose {index} is outside the bank's {bank.PoseCount}.");
        if (angles.Count < bank.PartCount)
            throw new InvalidDataException($"Pose {index} needs {bank.PartCount} joints, got {angles.Count}.");

        int at = bank.PoseDataOffset + index * bank.BytesPerPose;
        var data = bank.Data;

        // Root translation: three int16 with the halfword pair swapped, as the reader expects.
        void Root(int halfword, short value)
            => BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(at + (halfword ^ 1) * 2, 2), value);

        Root(0, rootTranslation.X);
        Root(1, rootTranslation.Y);
        Root(2, rootTranslation.Z);

        int start = at + PoseBank.PoseRootSize;
        int available = bank.BytesPerPose - PoseBank.PoseRootSize;

        ulong window = 0;
        int windowBits = 0;
        int cursor = 0;

        void Flush()
        {
            while (windowBits >= 32)
            {
                if (cursor + 4 <= available)
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(start + cursor, 4), (uint)(window & 0xFFFFFFFF));
                cursor += 4;
                window >>= 32;
                windowBits -= 32;
            }
        }

        void Push(int value)
        {
            if (value < 0 || value > MaxAngle)
                throw new InvalidDataException($"Angle {value} is outside 0..{MaxAngle}; wrap it before writing.");
            window |= (ulong)(uint)value << windowBits;
            windowBits += PoseBank.AngleBits;
            Flush();
        }

        for (int p = 0; p < bank.PartCount; p++)
        {
            Push(angles[p].X);
            Push(angles[p].Y);
            Push(angles[p].Z);
        }

        // Anything left in the window belongs to a final partial word; the bytes past the last angle
        // are padding the reader never looks at, so zero-filling them is safe and keeps output stable.
        if (windowBits > 0 && cursor + 4 <= available)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(start + cursor, 4), (uint)(window & 0xFFFFFFFF));
    }

    /// <summary>Converts a turn fraction (0..1) to the bank's 12-bit angle units, wrapping.</summary>
    public static int FromRadians(float radians)
    {
        double turns = radians / (2 * Math.PI);
        turns -= Math.Floor(turns);
        int value = (int)Math.Round(turns * PoseBank.AngleUnitsPerTurn);
        return value & MaxAngle;
    }

    /// <summary>One chunk asset that makes up a bank, and where its bytes sit in the assembled buffer.</summary>
    public sealed record BankChunk(int AssetId, int BufferOffset, int Length, int Repeat);

    /// <summary>
    /// Walks the index asset the way <see cref="PoseBank.Assemble"/> does, reporting where each
    /// contributing asset's bytes landed in the assembled buffer.
    /// </summary>
    public static List<BankChunk> Layout(RomFile rom, AssetDirectory directory, int indexAssetId)
    {
        var byId = new Dictionary<int, AssetEntry>();
        foreach (var e in directory.Entries) byId[e.Index] = e;

        if (!byId.TryGetValue(indexAssetId, out var indexEntry) || !directory.TryGetData(rom, indexEntry, out var index))
            throw new InvalidDataException($"Pose index asset {indexAssetId} is missing or undecodable.");

        // Assemble builds the header by swapping each halfword pair, so bytesPerPose -- which it
        // reads at buffer offset 4 -- comes from index offset 6, not 2.
        int bytesPerPose = BinaryPrimitives.ReadUInt16BigEndian(index.AsSpan(6, 2));
        int entryCount = BinaryPrimitives.ReadUInt16BigEndian(index.AsSpan(8, 2));

        var chunks = new List<BankChunk>();
        int cursor = 8;                                  // the two swapped header words come first

        for (int k = 0; k < entryCount; k++)
        {
            int at = 10 + k * 2;
            if (at + 2 > index.Length) break;

            ushort entry = BinaryPrimitives.ReadUInt16BigEndian(index.AsSpan(at, 2));
            int assetId = entry & 0x0FFF;
            int repeat = (entry >> 12) + 1;

            if (assetId == 0)
            {
                cursor += repeat * bytesPerPose;         // a run of empty poses, no asset behind it
                continue;
            }

            if (!byId.TryGetValue(assetId, out var part) || !directory.TryGetData(rom, part, out var chunk))
                throw new InvalidDataException($"Pose bank references asset {assetId}, which will not decode.");

            chunks.Add(new BankChunk(assetId, cursor, chunk.Length, repeat));
            cursor += chunk.Length * repeat;
        }

        return chunks;
    }

    /// <summary>Cuts an edited bank back into the assets it was assembled from.</summary>
    public static Dictionary<int, byte[]> SplitToAssets(byte[] assembled, IReadOnlyList<BankChunk> chunks,
                                                        out List<string> warnings)
    {
        warnings = new List<string>();
        var result = new Dictionary<int, byte[]>();

        foreach (var chunk in chunks)
        {
            if (chunk.BufferOffset + chunk.Length * chunk.Repeat > assembled.Length)
            {
                warnings.Add($"asset {chunk.AssetId} runs past the end of the bank and was skipped.");
                continue;
            }

            var bytes = assembled.AsSpan(chunk.BufferOffset, chunk.Length).ToArray();

            // Repeated chunks are one asset used several times; edits must agree across the repeats.
            for (int r = 1; r < chunk.Repeat; r++)
            {
                var other = assembled.AsSpan(chunk.BufferOffset + r * chunk.Length, chunk.Length);
                if (other.SequenceEqual(bytes)) continue;
                warnings.Add(
                    $"asset {chunk.AssetId} is used {chunk.Repeat} times in this bank, but repeat {r} " +
                    "no longer matches the first. Those poses share storage and cannot differ; " +
                    "the first copy was kept.");
                break;
            }

            if (result.TryGetValue(chunk.AssetId, out var existing) && !existing.AsSpan().SequenceEqual(bytes))
                warnings.Add($"asset {chunk.AssetId} appears more than once in the bank with different " +
                             "contents; the first was kept.");
            else
                result[chunk.AssetId] = bytes;
        }

        return result;
    }
}
