using System;

namespace Re2.Core.Codecs;

/// <summary>
/// Raw DEFLATE (RFC 1951) decoder that reports the exact number of input bytes consumed.
/// </summary>
public static class Inflate
{
    private const int MaxBits = 15;
    private const int MaxLitCodes = 286;
    private const int MaxDistCodes = 30;
    private const int FixedLitCodes = 288;

    private static readonly short[] LengthBase =
    {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
    };

    private static readonly short[] LengthExtra =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
    };

    private static readonly short[] DistBase =
    {
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577
    };

    private static readonly short[] DistExtra =
    {
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13
    };

    private static readonly byte[] CodeLengthOrder =
    {
        16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15
    };

    private sealed class Huffman
    {
        public readonly short[] Count = new short[MaxBits + 1];
        public readonly short[] Symbol;
        public Huffman(int symbols) => Symbol = new short[symbols];
    }

    private ref struct State
    {
        public ReadOnlySpan<byte> In;
        public int InPos;
        public int BitBuf;
        public int BitCnt;
        public byte[] Out;
        public int OutPos;
        public int MaxOut;

        /// <summary>Largest back-reference distance seen, i.e. the window this stream actually needs.</summary>
        public int MaxDistance;
    }

    /// <summary>
    /// Attempts to inflate a raw deflate stream starting at the beginning of <paramref name="input"/>.
    /// </summary>
    public static bool TryInflateRaw(ReadOnlySpan<byte> input, out byte[] output, out int consumed, int maxOutput = 64 << 20)
        => TryInflateRaw(input, out output, out consumed, out _, maxOutput);

    /// <summary>
    /// As <see cref="TryInflateRaw(ReadOnlySpan{byte}, out byte[], out int, int)"/>, additionally
    /// reporting the largest back-reference distance the stream uses.
    /// </summary>
    public static bool TryInflateRaw(ReadOnlySpan<byte> input, out byte[] output, out int consumed,
                                     out int maxDistance, int maxOutput = 64 << 20)
    {
        output = Array.Empty<byte>();
        consumed = 0;
        maxDistance = 0;

        var s = new State
        {
            In = input,
            InPos = 0,
            BitBuf = 0,
            BitCnt = 0,
            Out = new byte[Math.Min(1 << 16, Math.Max(1024, maxOutput))],
            OutPos = 0,
            MaxOut = maxOutput
        };

        try
        {
            bool last;
            do
            {
                last = Bits(ref s, 1) != 0;
                int type = Bits(ref s, 2);
                switch (type)
                {
                    case 0: Stored(ref s); break;
                    case 1: Codes(ref s, FixedLit.Value, FixedDist.Value); break;
                    case 2: Dynamic(ref s); break;
                    default: return false; // reserved block type
                }
            } while (!last);
        }
        catch (InflateError)
        {
            return false;
        }

        // Round the bit position up to the next byte boundary; that is where the stream ends.
        consumed = s.InPos;
        maxDistance = s.MaxDistance;
        output = s.Out.AsSpan(0, s.OutPos).ToArray();
        return true;
    }

    private sealed class InflateError : Exception { }

    private static InflateError Fail() => new();

    private static int Bits(ref State s, int need)
    {
        int val = s.BitBuf;
        while (s.BitCnt < need)
        {
            if (s.InPos >= s.In.Length) throw Fail();
            val |= s.In[s.InPos++] << s.BitCnt;
            s.BitCnt += 8;
        }
        s.BitBuf = val >> need;
        s.BitCnt -= need;
        return val & ((1 << need) - 1);
    }

    private static void EnsureOut(ref State s, int extra)
    {
        int need = s.OutPos + extra;
        if (need > s.MaxOut) throw Fail();
        if (need <= s.Out.Length) return;
        int cap = s.Out.Length;
        while (cap < need) cap = Math.Min(s.MaxOut, cap * 2);
        Array.Resize(ref s.Out, cap);
    }

    private static void Stored(ref State s)
    {
        // Stored blocks are byte-aligned; discard any partial bits.
        s.BitBuf = 0;
        s.BitCnt = 0;

        if (s.InPos + 4 > s.In.Length) throw Fail();
        int len = s.In[s.InPos] | (s.In[s.InPos + 1] << 8);
        int nlen = s.In[s.InPos + 2] | (s.In[s.InPos + 3] << 8);
        s.InPos += 4;
        if ((len ^ 0xFFFF) != nlen) throw Fail();
        if (s.InPos + len > s.In.Length) throw Fail();

        EnsureOut(ref s, len);
        s.In.Slice(s.InPos, len).CopyTo(s.Out.AsSpan(s.OutPos));
        s.InPos += len;
        s.OutPos += len;
    }

    private static int Decode(ref State s, Huffman h)
    {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len <= MaxBits; len++)
        {
            code |= Bits(ref s, 1);
            int count = h.Count[len];
            if (code - first < count) return h.Symbol[index + (code - first)];
            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }
        throw Fail();
    }

    private static void Construct(Huffman h, ReadOnlySpan<short> lengths, int n)
    {
        Array.Clear(h.Count);
        for (int i = 0; i < n; i++) h.Count[lengths[i]]++;
        if (h.Count[0] == n) return; // no codes at all

        // Reject over-subscribed sets. Incomplete sets are tolerated (valid for a single distance code).
        int left = 1;
        for (int len = 1; len <= MaxBits; len++)
        {
            left <<= 1;
            left -= h.Count[len];
            if (left < 0) throw Fail();
        }

        Span<short> offs = stackalloc short[MaxBits + 2];
        offs[1] = 0;
        for (int len = 1; len < MaxBits; len++) offs[len + 1] = (short)(offs[len] + h.Count[len]);
        for (int sym = 0; sym < n; sym++)
            if (lengths[sym] != 0) h.Symbol[offs[lengths[sym]]++] = (short)sym;
    }

    private static void Codes(ref State s, Huffman lit, Huffman dist)
    {
        while (true)
        {
            int sym = Decode(ref s, lit);
            if (sym < 256)
            {
                EnsureOut(ref s, 1);
                s.Out[s.OutPos++] = (byte)sym;
            }
            else if (sym == 256)
            {
                return;
            }
            else
            {
                sym -= 257;
                if (sym >= LengthBase.Length) throw Fail();
                int len = LengthBase[sym] + Bits(ref s, LengthExtra[sym]);

                int dsym = Decode(ref s, dist);
                if (dsym >= DistBase.Length) throw Fail();
                int d = DistBase[dsym] + Bits(ref s, DistExtra[dsym]);
                if (d > s.OutPos) throw Fail();
                if (d > s.MaxDistance) s.MaxDistance = d;

                EnsureOut(ref s, len);
                int from = s.OutPos - d;
                for (int i = 0; i < len; i++) s.Out[s.OutPos + i] = s.Out[from + i];
                s.OutPos += len;
            }
        }
    }

    private static void Dynamic(ref State s)
    {
        int nlen = Bits(ref s, 5) + 257;
        int ndist = Bits(ref s, 5) + 1;
        int ncode = Bits(ref s, 4) + 4;
        if (nlen > MaxLitCodes || ndist > MaxDistCodes) throw Fail();

        Span<short> lengths = stackalloc short[MaxLitCodes + MaxDistCodes];
        lengths.Clear();
        for (int i = 0; i < ncode; i++) lengths[CodeLengthOrder[i]] = (short)Bits(ref s, 3);
        for (int i = ncode; i < 19; i++) lengths[CodeLengthOrder[i]] = 0;

        var lenCode = new Huffman(19);
        Construct(lenCode, lengths, 19);

        int idx = 0;
        while (idx < nlen + ndist)
        {
            int sym = Decode(ref s, lenCode);
            if (sym < 16)
            {
                lengths[idx++] = (short)sym;
            }
            else
            {
                short prev = 0;
                int repeat;
                if (sym == 16)
                {
                    if (idx == 0) throw Fail();
                    prev = lengths[idx - 1];
                    repeat = 3 + Bits(ref s, 2);
                }
                else if (sym == 17)
                {
                    repeat = 3 + Bits(ref s, 3);
                }
                else
                {
                    repeat = 11 + Bits(ref s, 7);
                }

                if (idx + repeat > nlen + ndist) throw Fail();
                while (repeat-- > 0) lengths[idx++] = prev;
            }
        }

        if (lengths[256] == 0) throw Fail(); // no end-of-block code

        var lit = new Huffman(MaxLitCodes);
        var dist = new Huffman(MaxDistCodes);
        Construct(lit, lengths[..nlen], nlen);
        Construct(dist, lengths.Slice(nlen, ndist), ndist);
        Codes(ref s, lit, dist);
    }

    private static readonly Lazy<Huffman> FixedLit = new(() =>
    {
        Span<short> l = stackalloc short[FixedLitCodes];
        for (int i = 0; i < 144; i++) l[i] = 8;
        for (int i = 144; i < 256; i++) l[i] = 9;
        for (int i = 256; i < 280; i++) l[i] = 7;
        for (int i = 280; i < FixedLitCodes; i++) l[i] = 8;
        var h = new Huffman(FixedLitCodes);
        Construct(h, l, FixedLitCodes);
        return h;
    });

    private static readonly Lazy<Huffman> FixedDist = new(() =>
    {
        Span<short> l = stackalloc short[MaxDistCodes];
        for (int i = 0; i < MaxDistCodes; i++) l[i] = 5;
        var h = new Huffman(MaxDistCodes);
        Construct(h, l, MaxDistCodes);
        return h;
    });
}
