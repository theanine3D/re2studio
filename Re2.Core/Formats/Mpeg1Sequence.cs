using System;

namespace Re2.Core.Formats;

/// <summary>
/// The MPEG-1 sequence header, and the few stream-level operations the FMV tab needs.
/// </summary>
public static class Mpeg1Sequence
{
    public const byte PictureStart = 0x00;
    public const byte UserData = 0xB2;
    public const byte SequenceHeader = 0xB3;
    public const byte SequenceError = 0xB4;
    public const byte Extension = 0xB5;
    public const byte SequenceEnd = 0xB7;
    public const byte GroupStart = 0xB8;

    /// <summary>Start code plus the fixed part of the header, in bytes.</summary>
    public const int HeaderSize = 12;

    /// <summary>The bit_rate value meaning "variable", which is what PC encoders write.</summary>
    public const int VariableBitRate = 0x3FFFF;

    /// <summary>What the retail movies declare, and therefore what an import is made to declare.</summary>
    public const int RetailBitRateCode = 1000;         // 400 kbit/s, in 400 bit/s units
    public const int RetailVbvBufferSize = 112;

    public readonly record struct Header(
        int Width, int Height, int AspectRatioCode, int FrameRateCode,
        int BitRateCode, int VbvBufferSize,
        bool ConstrainedParameters, bool LoadIntraMatrix, bool LoadNonIntraMatrix);

    public static bool StartsWith(ReadOnlySpan<byte> data, byte code)
        => data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == code;

    /// <summary>The offset of the next start code of this kind, or -1.</summary>
    public static int Find(ReadOnlySpan<byte> data, byte code, int from = 0, int limit = int.MaxValue)
    {
        int end = (int)Math.Min((long)data.Length - 4, (long)limit);

        for (int i = Math.Max(0, from); i <= end; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && data[i + 3] == code) return i;

        return -1;
    }

    public static int Count(ReadOnlySpan<byte> data, byte code)
    {
        int found = 0;
        for (int i = 0; i + 4 <= data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && data[i + 3] == code) found++;

        return found;
    }

    /// <summary>How long the sequence header is, including the start code.</summary>
    public static int HeaderLength(ReadOnlySpan<byte> data)
    {
        var header = Read(data);
        return HeaderSize + (header.LoadIntraMatrix ? 64 : 0) + (header.LoadNonIntraMatrix ? 64 : 0);
    }

    public static Header Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new ArgumentException("Too short to hold a sequence header.");

        var bits = new BitCursor(data, 32);

        int width = bits.Read(12);
        int height = bits.Read(12);
        int aspect = bits.Read(4);
        int rate = bits.Read(4);
        int bitRate = bits.Read(18);
        bits.Read(1);                                   // marker
        int vbv = bits.Read(10);

        return new Header(width, height, aspect, rate, bitRate, vbv,
                          bits.Read(1) != 0, bits.Read(1) != 0, bits.Read(1) != 0);
    }

    /// <summary>
    /// Rewrites the rate fields in place, leaving everything else -- including any quantiser matrices
    /// that follow -- exactly as it was.
    /// </summary>
    public static void SetRate(Span<byte> data, int bitRateCode, int vbvBufferSize)
    {
        var header = Read(data);

        uint word = ((uint)(bitRateCode & 0x3FFFF) << 14)
                    | (1u << 13)                                      // marker
                    | ((uint)(vbvBufferSize & 0x3FF) << 3)
                    | (header.ConstrainedParameters ? 4u : 0u)
                    | (header.LoadIntraMatrix ? 2u : 0u)
                    | (header.LoadNonIntraMatrix ? 1u : 0u);

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data[8..], word);
    }

    /// <summary>Reads big-endian bits, most significant first, as MPEG streams are written.</summary>
    private ref struct BitCursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public BitCursor(ReadOnlySpan<byte> data, int startBit)
        {
            _data = data;
            _position = startBit;
        }

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int index = _position >> 3;
                int bit = index >= _data.Length ? 0 : (_data[index] >> (7 - (_position & 7))) & 1;
                value = (value << 1) | bit;
                _position++;
            }
            return value;
        }
    }
}
