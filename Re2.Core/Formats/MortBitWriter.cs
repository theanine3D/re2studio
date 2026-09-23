using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Re2.Core.Formats;

/// <summary>Writes bits the way the game's MORT decoder reads them.</summary>
public sealed class MortBitWriter
{
    private readonly List<uint> _words = new();
    private int _position;

    /// <summary>The bit position writing will continue from.</summary>
    public int Position => _position;

    public MortBitWriter(int startBit = 0)
    {
        _position = startBit;
        while (_words.Count < (startBit + 31) / 32) _words.Add(0);
    }

    /// <summary>Continues an existing stream, keeping everything already written.</summary>
    public MortBitWriter(ReadOnlySpan<byte> existing, int startBit)
    {
        for (int at = 0; at + 4 <= existing.Length; at += 4)
            _words.Add(BinaryPrimitives.ReadUInt32BigEndian(existing[at..]));

        _position = startBit;
        while (_words.Count < (startBit + 31) / 32) _words.Add(0);
    }

    /// <summary>Writes <paramref name="count"/> low bits of <paramref name="value"/>.</summary>
    public void Write(int value, int count)
    {
        if (count is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(count));

        for (int i = 0; i < count; i++)
        {
            int bit = (value >> i) & 1;
            int word = _position >> 5, offset = _position & 31;

            while (_words.Count <= word) _words.Add(0);
            _words[word] |= (uint)bit << offset;

            _position++;
        }
    }

    /// <summary>The stream so far, padded out to a whole number of 32-bit words.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[_words.Count * 4];
        for (int i = 0; i < _words.Count; i++)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), _words[i]);
        return bytes;
    }
}
