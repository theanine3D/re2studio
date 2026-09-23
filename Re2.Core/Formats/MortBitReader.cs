using System;
using System.Buffers.Binary;

namespace Re2.Core.Formats;

/// <summary>
/// Reads bits the way the game's MORT decoder does: least-significant-first inside each big-endian
/// 32-bit word. The exact inverse of <see cref="MortBitWriter"/>.
/// </summary>
public sealed class MortBitReader
{
    private readonly byte[] _data;
    private int _position;

    public MortBitReader(ReadOnlySpan<byte> data, int startBit)
    {
        _data = data.ToArray();
        _position = startBit;
    }

    /// <summary>The bit position reading will continue from.</summary>
    public int Position => _position;

    private uint Word(int index)
    {
        int at = index * 4;
        if (at + 4 <= _data.Length) return BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at));

        // Past the end reads as zero, exactly as the decoder's ring buffer does once a clip runs out.
        uint value = 0;
        for (int i = 0; i < 4; i++)
            value = (value << 8) | (at + i < _data.Length ? _data[at + i] : 0u);
        return value;
    }

    public int Read(int count)
    {
        int value = 0;

        for (int i = 0; i < count; i++)
        {
            int word = _position >> 5, offset = _position & 31;
            value |= (int)((Word(word) >> offset) & 1) << i;
            _position++;
        }

        return value;
    }
}
