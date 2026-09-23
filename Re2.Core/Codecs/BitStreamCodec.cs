using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Re2.Core.Codecs;

/// <summary>Result of expanding a bitstream-coded asset.</summary>
public sealed record BitStreamResult(uint[] Data, int HeaderCount)
{
    /// <summary>The leading (offset, count) records, decoded from the header portion.</summary>
    public IReadOnlyList<(int Offset, int Count)> Table
    {
        get
        {
            var table = new List<(int, int)>(HeaderCount);
            for (int i = 0; i < HeaderCount; i++)
                table.Add(((int)(Data[i] >> 16), (int)(Data[i] & 0xFFFF)));
            return table;
        }
    }
}

/// <summary>
/// RE2's custom bit-level codec, used for the animation assets that sit behind the ordinary
/// deflate/stored layer -- a second compression stage the asset directory knows nothing about.
/// </summary>
public static class BitStreamCodec
{
    /// <summary>Ceiling on decoder iterations, so a non-conforming input cannot spin forever.</summary>
    private const int MaxIterations = 400_000;

    /// <summary>Ceiling on output words.</summary>
    private const int MaxWords = 1 << 20;

    private ref struct Reader
    {
        public ReadOnlySpan<byte> Source;
        public int Position;
        public uint Window;
        public int Available;

        public uint Read(int bits)
        {
            uint value = Window & (bits >= 32 ? uint.MaxValue : (1u << bits) - 1);
            Window >>= bits;
            Available -= bits;

            if (Available < 16)
            {
                ushort next = Position + 2 <= Source.Length
                    ? BinaryPrimitives.ReadUInt16BigEndian(Source.Slice(Position, 2))
                    : (ushort)0;
                Position += 2;
                Window |= (uint)next << Available;
                Available += 16;
            }

            return value;
        }

        public void Skip(int bits) => Read(bits);
    }

    /// <summary>Run length. Null means the escape code, which switches to the mask coder.</summary>
    private static int? ReadRunLength(ref Reader r)
    {
        uint w = r.Window;
        if ((w & 1) != 0) { r.Skip(1); return 1; }
        if ((w & 2) != 0) { r.Skip(2); return 2; }
        if ((w & 4) != 0) { r.Skip(3); return 3; }
        if ((w & 8) != 0) return (int)(r.Read(12) >> 4);
        if ((w & 0x10) != 0) return (int)(r.Read(16) >> 5);
        r.Skip(16);
        return null;
    }

    private static uint ReadDelta(ref Reader r)
    {
        uint w = r.Window;
        if ((w & 1) != 0) { r.Skip(1); return 2; }
        if ((w & 2) != 0) { r.Skip(2); return 1; }
        if ((w & 4) != 0) { r.Skip(3); return 0; }
        if ((w & 8) != 0) return r.Read(8) >> 4;
        if ((w & 0x10) != 0) return r.Read(14) >> 5;
        if ((w & 0x20) != 0) { r.Skip(6); return ~r.Read(2); }
        r.Skip(7);
        return r.Read(12) | 0xFFFFF000;
    }

    /// <summary>Expands a bitstream-coded asset.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> source, out BitStreamResult result)
    {
        result = null!;
        if (source.Length < 8) return false;

        var reader = new Reader
        {
            Source = source,
            Position = 4,
            Window = ((uint)BinaryPrimitives.ReadUInt16BigEndian(source.Slice(2, 2)) << 16)
                     | BinaryPrimitives.ReadUInt16BigEndian(source[..2]),
            Available = 32
        };

        var output = new List<uint>();
        int headerCount = (int)reader.Read(8);

        for (int i = 0; i < headerCount; i++)
        {
            uint count = reader.Read(9);
            uint offset = reader.Read(15);
            output.Add(count | (offset << 16));
        }

        // The seed is not a preamble: it is the first frame word, and the delta chain continues
        // straight on from it. Everything the table's offsets point at starts here.
        output.Add(reader.Read(12));
        int maskCursor = output.Count - 1;

        uint accumulator = output[maskCursor] << 1;
        int guard = 0;

        while (true)
        {
            if (++guard > MaxIterations || output.Count > MaxWords) return false;

            int? run = ReadRunLength(ref reader);

            if (run is null)
            {
                // Escape: the mask coder ORs values into positions relative to the header.
                int index = 0;
                while (true)
                {
                    if (++guard > MaxIterations) return false;

                    uint w = reader.Window;
                    int repeat;
                    if ((w & 1) != 0) { reader.Skip(1); repeat = 1; }
                    else if ((w & 2) != 0) repeat = (int)(reader.Read(10) >> 2);
                    else
                    {
                        reader.Skip(16);
                        result = new BitStreamResult(output.ToArray(), headerCount);
                        return true;
                    }

                    w = reader.Window;
                    // All three bits clear leaves the previous index in place; that is a real
                    // "same slot again" code, not a decoding slip.
                    if ((w & 1) != 0) index = (int)(reader.Read(5) >> 1);
                    else if ((w & 2) != 0) index = (int)(reader.Read(8) >> 2) + 0x10;
                    else if ((w & 4) != 0) index = (int)(reader.Read(13) >> 3) + 0x50;

                    uint flags = reader.Window;
                    reader.Skip(3);

                    uint mask = 0;
                    if ((flags & 1) != 0) mask = reader.Read(4) << 12;
                    if ((flags & 2) != 0) mask |= reader.Read(8) << 16;
                    if ((flags & 4) != 0) mask |= reader.Read(8) << 24;

                    int at = maskCursor + index;
                    for (int i = 0; i < repeat; i++)
                    {
                        while (output.Count <= at) output.Add(0);
                        output[at] |= mask;
                        at++;
                    }
                    maskCursor = at - 1;
                }
            }

            uint delta = ReadDelta(ref reader);
            accumulator &= 0xFFFFFFFE;

            for (int i = 0; i < run.Value; i++)
            {
                accumulator += delta;
                output.Add(accumulator >> 1);
            }
        }
    }

    /// <summary>
    /// True when the decoded header chains consistently: each record must start exactly where the
    /// previous one ends.
    /// </summary>
    public static bool HeaderChains(BitStreamResult result)
    {
        var table = result.Table;
        int checkedPairs = 0;

        for (int i = 0; i + 1 < table.Count; i++)
        {
            if (table[i].Count == 0) break;               // trailing empty slots
            if (table[i + 1].Offset == 0) break;
            if (table[i + 1].Offset != table[i].Offset + table[i].Count * 4) return false;
            checkedPairs++;
        }

        return checkedPairs > 0;
    }
}
