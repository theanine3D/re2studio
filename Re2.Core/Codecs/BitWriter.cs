using System;
using System.IO;

namespace Re2.Core.Codecs;

/// <summary>
/// The bit order DEFLATE writes in: values least-significant bit first, packed into bytes from bit 0
/// upwards.
/// </summary>
public sealed class BitWriter
{
    private readonly MemoryStream _output = new();
    private uint _bits;
    private int _held;

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>.</summary>
    public void Write(int value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _bits |= (uint)((value >> i) & 1) << _held;

            if (++_held == 8)
            {
                _output.WriteByte((byte)_bits);
                _bits = 0;
                _held = 0;
            }
        }
    }

    /// <summary>Writes a Huffman code, which the specification packs the other way round.</summary>
    public void Huffman(int code, int count)
    {
        for (int i = count - 1; i >= 0; i--) Write((code >> i) & 1, 1);
    }

    /// <summary>Pads to the next byte boundary, as a stored block's header requires.</summary>
    public void Align()
    {
        if (_held == 0) return;

        _output.WriteByte((byte)_bits);
        _bits = 0;
        _held = 0;
    }

    /// <summary>The body of a stored block: its length, its complement, and the bytes themselves.</summary>
    public void Stored(ReadOnlySpan<byte> data)
    {
        // Stored blocks cap at 65535 bytes, so a long one becomes several.
        _output.WriteByte((byte)(data.Length & 0xFF));
        _output.WriteByte((byte)((data.Length >> 8) & 0xFF));
        _output.WriteByte((byte)(~data.Length & 0xFF));
        _output.WriteByte((byte)((~data.Length >> 8) & 0xFF));
        _output.Write(data);
    }

    public byte[] ToArray()
    {
        Align();
        return _output.ToArray();
    }
}
