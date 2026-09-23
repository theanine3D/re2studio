using System;
using System.Collections.Generic;
using System.Text;

namespace Re2.Core.Patch;

/// <summary>
/// Creates and applies BPS patches: the difference between an original ROM and a modified one.
/// </summary>
public static class BpsPatch
{
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("BPS1");

    /// <summary>The four actions a BPS patch is built from; only the first two are ever written here.</summary>
    private const int SourceRead = 0, TargetRead = 1, SourceCopy = 2, TargetCopy = 3;

    /// <summary>
    /// Builds a patch turning <paramref name="source"/> into <paramref name="target"/>.
    /// </summary>
    public static byte[] Create(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target, string metadata = "")
    {
        var patch = new List<byte>(1 << 20);
        patch.AddRange(Signature);

        WriteNumber(patch, (ulong)source.Length);
        WriteNumber(patch, (ulong)target.Length);

        var metadataBytes = Encoding.UTF8.GetBytes(metadata);
        WriteNumber(patch, (ulong)metadataBytes.Length);
        patch.AddRange(metadataBytes);

        int at = 0;
        while (at < target.Length)
        {
            // How far the two agree from here. Past the end of the source nothing can agree.
            int same = 0;
            while (at + same < target.Length &&
                   at + same < source.Length &&
                   source[at + same] == target[at + same]) same++;

            if (same > 0)
            {
                WriteAction(patch, SourceRead, same);
                at += same;
                continue;
            }

            int differs = 0;
            while (at + differs < target.Length &&
                   (at + differs >= source.Length || source[at + differs] != target[at + differs])) differs++;

            WriteAction(patch, TargetRead, differs);
            for (int i = 0; i < differs; i++) patch.Add(target[at + i]);
            at += differs;
        }

        WriteChecksum(patch, Crc32.Compute(source));
        WriteChecksum(patch, Crc32.Compute(target));

        // The patch's own checksum covers everything written so far, itself excluded.
        var soFar = patch.ToArray();
        WriteChecksum(patch, Crc32.Compute(soFar));

        return patch.ToArray();
    }

    /// <summary>What applying a patch produced, and what the patch said about itself.</summary>
    public sealed record Applied(byte[] Target, string Metadata, uint SourceChecksum, uint TargetChecksum);

    /// <summary>Applies a patch to a ROM.</summary>
    public static Applied Apply(ReadOnlySpan<byte> patch, ReadOnlySpan<byte> source)
    {
        const int FooterSize = 12;

        if (patch.Length < 4 + FooterSize || !patch[..4].SequenceEqual(Signature))
            throw new InvalidOperationException("This is not a BPS patch.");

        if (Crc32.Compute(patch[..^4]) != ReadChecksum(patch, patch.Length - 4))
            throw new InvalidOperationException(
                "This patch file is damaged: its own checksum does not match. Try downloading it again.");

        int end = patch.Length - FooterSize;
        int at = 4;

        long sourceSize = (long)ReadNumber(patch, ref at, end);
        long targetSize = (long)ReadNumber(patch, ref at, end);
        long metadataSize = (long)ReadNumber(patch, ref at, end);

        if (metadataSize < 0 || at + metadataSize > end)
            throw new InvalidOperationException("This patch file is damaged: its header runs past the end.");

        string metadata = Encoding.UTF8.GetString(patch.Slice(at, (int)metadataSize));
        at += (int)metadataSize;

        if (sourceSize != source.Length)
            throw new InvalidOperationException(
                $"This patch expects a {sourceSize:N0}-byte ROM, but the one chosen is {source.Length:N0} bytes. " +
                "It is probably for a different version, or for a ROM with or without a header.");

        uint sourceChecksum = ReadChecksum(patch, patch.Length - 12);
        if (Crc32.Compute(source) != sourceChecksum)
            throw new InvalidOperationException(
                "This patch is not for this ROM. The sizes match but the contents do not, so it was " +
                "made from a different copy or a different region.");

        if (targetSize is < 0 or > (1L << 32))
            throw new InvalidOperationException("This patch file is damaged: an implausible output size.");

        var target = new byte[targetSize];
        int output = 0, sourceRelative = 0, targetRelative = 0;

        while (at < end)
        {
            ulong packed = ReadNumber(patch, ref at, end);
            int action = (int)(packed & 3);
            long length = (long)(packed >> 2) + 1;

            if (output + length > targetSize)
                throw new InvalidOperationException("This patch file is damaged: it writes past the end of the ROM.");

            switch (action)
            {
                case SourceRead:
                    if (output + length > source.Length) throw Damaged();
                    for (long i = 0; i < length; i++, output++) target[output] = source[output];
                    break;

                case TargetRead:
                    if (at + length > end) throw Damaged();
                    for (long i = 0; i < length; i++, output++) target[output] = patch[at++];
                    break;

                case SourceCopy:
                    sourceRelative += SignedOffset(patch, ref at, end);
                    if (sourceRelative < 0 || sourceRelative + length > source.Length) throw Damaged();
                    for (long i = 0; i < length; i++) target[output++] = source[sourceRelative++];
                    break;

                default:
                    targetRelative += SignedOffset(patch, ref at, end);

                    // Only the first byte has to exist yet.
                    if (targetRelative < 0 || targetRelative >= output) throw Damaged();
                    for (long i = 0; i < length; i++) target[output++] = target[targetRelative++];
                    break;
            }
        }

        if (output != targetSize)
            throw new InvalidOperationException("This patch file is damaged: it did not fill the whole ROM.");

        uint targetChecksum = ReadChecksum(patch, patch.Length - 8);
        if (Crc32.Compute(target) != targetChecksum)
            throw new InvalidOperationException(
                "The patched ROM does not match what the patch says it should produce. The patch file " +
                "is probably damaged.");

        return new Applied(target, metadata, sourceChecksum, targetChecksum);
    }

    private static InvalidOperationException Damaged()
        => new("This patch file is damaged: it reads outside the ROM.");

    private static int SignedOffset(ReadOnlySpan<byte> patch, ref int at, int end)
    {
        ulong raw = ReadNumber(patch, ref at, end);
        int magnitude = (int)(raw >> 1);
        return (raw & 1) != 0 ? -magnitude : magnitude;
    }

    private static void WriteAction(List<byte> patch, int action, int length)
        => WriteNumber(patch, ((ulong)(length - 1) << 2) | (uint)action);

    /// <summary>
    /// BPS's variable-length number: seven bits at a time, the top bit marking the last byte, and
    /// each continuation implicitly adding one so no value has two encodings.
    /// </summary>
    private static void WriteNumber(List<byte> patch, ulong value)
    {
        while (true)
        {
            byte low = (byte)(value & 0x7F);
            value >>= 7;

            if (value == 0) { patch.Add((byte)(0x80 | low)); return; }

            patch.Add(low);
            value--;
        }
    }

    private static ulong ReadNumber(ReadOnlySpan<byte> patch, ref int at, int end)
    {
        ulong value = 0, shift = 1;

        while (true)
        {
            if (at >= end) throw Damaged();
            byte x = patch[at++];
            value += (x & 0x7FUL) * shift;
            if ((x & 0x80) != 0) return value;
            shift <<= 7;
            value += shift;
        }
    }

    private static void WriteChecksum(List<byte> patch, uint value)
    {
        patch.Add((byte)value);
        patch.Add((byte)(value >> 8));
        patch.Add((byte)(value >> 16));
        patch.Add((byte)(value >> 24));
    }

    private static uint ReadChecksum(ReadOnlySpan<byte> patch, int at)
        => (uint)(patch[at] | (patch[at + 1] << 8) | (patch[at + 2] << 16) | (patch[at + 3] << 24));
}

/// <summary>CRC32 as BPS uses it, which is the ordinary reflected polynomial.</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
