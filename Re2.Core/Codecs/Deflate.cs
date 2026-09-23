using System;
using System.Collections.Generic;

namespace Re2.Core.Codecs;

/// <summary>A DEFLATE encoder whose back-references are bounded by a window we choose.</summary>
public static class Deflate
{
    public const int MinMatch = 3, MaxMatch = 258;

    /// <summary>How far the match finder will walk one hash chain before settling for what it has.</summary>
    private const int MaxChain = 2048;

    /// <summary>The smallest range block splitting will consider cutting further.</summary>
    private const int MinBlockTokens = 512;

    /// <summary>How many split points to try across a range. More is slower and barely better.</summary>
    private const int SplitProbes = 12;

    private const int HashBits = 16, HashSize = 1 << HashBits;

    /// <summary>Compresses to a raw deflate stream that never reaches back past <paramref name="window"/>.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, int window)
    {
        var tokens = new Tokens();

        int[] head = new int[HashSize];
        Array.Fill(head, -1);
        int[] prev = new int[Math.Max(1, data.Length)];

        int at = 0;

        // Positions below this are in the chains.
        int inserted = 0;

        while (at < data.Length)
        {
            while (inserted < at && inserted + MinMatch <= data.Length)
                Insert(data, head, prev, inserted++);

            int length = 0, distance = 0;

            if (at + MinMatch <= data.Length)
                Find(data, head, prev, at, window, out length, out distance);

            // Lazy matching: a match starting one byte later may be long enough to be worth
            // emitting this byte as a literal instead.
            if (length is >= MinMatch and < MaxMatch && at + 1 + MinMatch <= data.Length)
            {
                while (inserted <= at && inserted + MinMatch <= data.Length)
                    Insert(data, head, prev, inserted++);

                Find(data, head, prev, at + 1, window, out int next, out int nextDistance);

                if (next > length)
                {
                    tokens.Literal(at, data[at]);
                    at++;
                    length = next;
                    distance = nextDistance;
                }
            }

            if (length >= MinMatch) { tokens.Match(at, length, distance); at += length; }
            else { tokens.Literal(at, data[at]); at++; }
        }

        tokens.Close(data.Length);

        var bits = new BitWriter();
        var cuts = new List<int> { 0 };
        Split(tokens, data, 0, tokens.Count, cuts);
        cuts.Add(tokens.Count);

        for (int i = 0; i + 1 < cuts.Count; i++)
            tokens.Write(bits, data, cuts[i], cuts[i + 1], last: i + 2 == cuts.Count);

        return bits.ToArray();
    }

    /// <summary>
    /// Finds block boundaries worth having, by asking whether cutting a range in two costs less than
    /// leaving it whole, and recursing into each half that it does.
    /// </summary>
    private static void Split(Tokens tokens, ReadOnlySpan<byte> data, int from, int to,
                              List<int> cuts)
    {
        if (to - from < MinBlockTokens * 2) return;

        long bestCost = tokens.Cost(data, from, to);
        int best = -1;

        for (int probe = 1; probe <= SplitProbes; probe++)
        {
            int cut = from + (to - from) * probe / (SplitProbes + 1);
            if (cut - from < MinBlockTokens || to - cut < MinBlockTokens) continue;

            long cost = tokens.Cost(data, from, cut) + tokens.Cost(data, cut, to);
            if (cost < bestCost) { bestCost = cost; best = cut; }
        }

        if (best < 0) return;

        Split(tokens, data, from, best, cuts);
        cuts.Add(best);
        Split(tokens, data, best, to, cuts);
    }

    private static int Hash(ReadOnlySpan<byte> data, int at)
        => ((data[at] << 10) ^ (data[at + 1] << 5) ^ data[at + 2]) & (HashSize - 1);

    private static void Insert(ReadOnlySpan<byte> data, int[] head, int[] prev, int at)
    {
        int h = Hash(data, at);
        prev[at] = head[h];
        head[h] = at;
    }

    /// <summary>The longest match for the bytes at <paramref name="at"/>, within the window.</summary>
    private static void Find(ReadOnlySpan<byte> data, int[] head, int[] prev, int at, int window,
                             out int length, out int distance)
    {
        length = 0;
        distance = 0;

        if (at + MinMatch > data.Length) return;

        int limit = Math.Max(0, at - window);
        int most = Math.Min(MaxMatch, data.Length - at);
        int candidate = head[Hash(data, at)];

        for (int chain = 0; chain < MaxChain && candidate >= limit; chain++)
        {
            // Cheap rejection before the compare: the byte that would extend the current best.
            if (data[candidate + length] == data[at + length])
            {
                int n = 0;
                while (n < most && data[candidate + n] == data[at + n]) n++;

                if (n > length)
                {
                    length = n;
                    distance = at - candidate;
                    if (n >= most) break;
                }
            }

            candidate = prev[candidate];
        }

        if (length < MinMatch) { length = 0; distance = 0; }
    }

    // ---- symbol tables -------------------------------------------------------------------------

    private static readonly int[] LengthBase =
    {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115,
        131, 163, 195, 227, 258,
    };

    private static readonly int[] LengthExtra =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
    };

    private static readonly int[] DistanceBase =
    {
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537,
        2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
    };

    private static readonly int[] DistanceExtra =
    {
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12,
        13, 13,
    };

    /// <summary>The order the code-length code lengths are written in, from the specification.</summary>
    private static readonly int[] CodeLengthOrder =
        { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    private static int LengthCode(int length)
    {
        for (int i = LengthBase.Length - 1; i >= 0; i--)
            if (length >= LengthBase[i]) return i;
        return 0;
    }

    private static int DistanceCode(int distance)
    {
        for (int i = DistanceBase.Length - 1; i >= 0; i--)
            if (distance >= DistanceBase[i]) return i;
        return 0;
    }

    // ---- the parsed input ----------------------------------------------------------------------

    /// <summary>
    /// Every literal and match the parser produced, over the whole input, so that block boundaries can
    /// be chosen once the shape of the data is known rather than guessed at as it arrives.
    /// </summary>
    private sealed class Tokens
    {
        private readonly List<int> _lengths = new();
        private readonly List<int> _values = new();
        private readonly List<int> _starts = new();

        public int Count => _values.Count;

        public void Literal(int at, byte b) { _lengths.Add(0); _values.Add(b); _starts.Add(at); }

        public void Match(int at, int length, int distance)
        {
            _lengths.Add(length);
            _values.Add(distance);
            _starts.Add(at);
        }

        /// <summary>Records where the last token ends, so the final range has a byte extent too.</summary>
        public void Close(int end) => _starts.Add(end);

        /// <summary>What a range of tokens would cost as one block, in bits.</summary>
        public long Cost(ReadOnlySpan<byte> data, int from, int to)
            => Math.Min(Plan(from, to).Dynamic, Stored(from, to));

        public void Write(BitWriter bits, ReadOnlySpan<byte> data, int from, int to, bool last)
        {
            var plan = Plan(from, to);

            if (Stored(from, to) < plan.Dynamic)
            {
                bits.Write(last ? 1 : 0, 1);
                bits.Write(0, 2);
                bits.Align();
                bits.Stored(data[_starts[from].._starts[to]]);
                return;
            }

            bits.Write(last ? 1 : 0, 1);
            bits.Write(2, 2);                            // dynamic Huffman
            bits.Write(plan.Hlit - 257, 5);
            bits.Write(plan.Hdist - 1, 5);
            bits.Write(plan.Hclen - 4, 4);

            for (int i = 0; i < plan.Hclen; i++)
                bits.Write(plan.CodeLengthBits[CodeLengthOrder[i]], 3);

            var codeLengthCodes = Huffman.Codes(plan.CodeLengthBits);
            foreach (var item in plan.Packed)
            {
                bits.Huffman(codeLengthCodes[item.Symbol], plan.CodeLengthBits[item.Symbol]);
                if (item.Extra > 0) bits.Write(item.Value, item.Extra);
            }

            var literalCodes = Huffman.Codes(plan.LiteralBits);
            var distanceCodes = Huffman.Codes(plan.DistanceBits);

            for (int i = from; i < to; i++)
            {
                if (_lengths[i] == 0)
                {
                    int b = _values[i];
                    bits.Huffman(literalCodes[b], plan.LiteralBits[b]);
                    continue;
                }

                int code = LengthCode(_lengths[i]);
                bits.Huffman(literalCodes[257 + code], plan.LiteralBits[257 + code]);
                if (LengthExtra[code] > 0)
                    bits.Write(_lengths[i] - LengthBase[code], LengthExtra[code]);

                int dcode = DistanceCode(_values[i]);
                bits.Huffman(distanceCodes[dcode], plan.DistanceBits[dcode]);
                if (DistanceExtra[dcode] > 0)
                    bits.Write(_values[i] - DistanceBase[dcode], DistanceExtra[dcode]);
            }

            bits.Huffman(literalCodes[256], plan.LiteralBits[256]);
        }

        /// <summary>What storing this range verbatim would cost.</summary>
        private long Stored(int from, int to)
        {
            int bytes = _starts[to] - _starts[from];
            return bytes > 0xFFFF ? long.MaxValue : 3 + 7 + (bytes + 4L) * 8;
        }

        private readonly record struct Packed(int Symbol, int Value, int Extra);

        private sealed record Layout(int[] LiteralBits, int[] DistanceBits, int[] CodeLengthBits,
                                     List<Packed> Packed, int Hlit, int Hdist, int Hclen,
                                     long Dynamic);

        /// <summary>Builds the tables for a range and prices the block they would make.</summary>
        private Layout Plan(int from, int to)
        {
            var literalFreq = new long[286];
            var distanceFreq = new long[30];
            literalFreq[256] = 1;                        // end of block

            for (int i = from; i < to; i++)
            {
                if (_lengths[i] == 0) literalFreq[_values[i]]++;
                else
                {
                    literalFreq[257 + LengthCode(_lengths[i])]++;
                    distanceFreq[DistanceCode(_values[i])]++;
                }
            }

            var literalBits = Huffman.Lengths(literalFreq, 15);
            var distanceBits = Huffman.Lengths(distanceFreq, 15);

            // A block with no matches at all still has to declare one distance code.
            int hdist = 1;
            for (int i = 0; i < 30; i++) if (distanceBits[i] != 0) hdist = i + 1;
            int hlit = 257;
            for (int i = 0; i < 286; i++) if (literalBits[i] != 0) hlit = i + 1;

            var packed = PackCodeLengths(literalBits, hlit, distanceBits, hdist);

            var codeLengthFreq = new long[19];
            foreach (var item in packed) codeLengthFreq[item.Symbol]++;
            var codeLengthBits = Huffman.Lengths(codeLengthFreq, 7);

            int hclen = 4;
            for (int i = 0; i < 19; i++) if (codeLengthBits[CodeLengthOrder[i]] != 0) hclen = i + 1;

            long total = 3 + 14 + hclen * 3L + literalBits[256];
            foreach (var item in packed) total += codeLengthBits[item.Symbol] + item.Extra;

            for (int i = from; i < to; i++)
            {
                if (_lengths[i] == 0) { total += literalBits[_values[i]]; continue; }

                int code = LengthCode(_lengths[i]);
                int dcode = DistanceCode(_values[i]);
                total += literalBits[257 + code] + LengthExtra[code]
                       + distanceBits[dcode] + DistanceExtra[dcode];
            }

            return new Layout(literalBits, distanceBits, codeLengthBits, packed,
                              hlit, hdist, hclen, total);
        }

        /// <summary>
        /// The two code-length tables run together and run-length encoded: 16 repeats the previous
        /// length, 17 and 18 run zeroes.
        /// </summary>
        private static List<Packed> PackCodeLengths(int[] literalBits, int hlit,
                                                    int[] distanceBits, int hdist)
        {
            var all = new int[hlit + hdist];
            Array.Copy(literalBits, all, hlit);
            Array.Copy(distanceBits, 0, all, hlit, hdist);

            var packed = new List<Packed>();

            for (int i = 0; i < all.Length; )
            {
                int value = all[i], run = 1;
                while (i + run < all.Length && all[i + run] == value) run++;

                if (value == 0)
                {
                    while (run >= 11) { int take = Math.Min(138, run); packed.Add(new Packed(18, take - 11, 7)); run -= take; i += take; }
                    while (run >= 3) { int take = Math.Min(10, run); packed.Add(new Packed(17, take - 3, 3)); run -= take; i += take; }
                    while (run-- > 0) { packed.Add(new Packed(0, 0, 0)); i++; }
                    continue;
                }

                // The first one is written plainly; only what follows it can be a repeat.
                packed.Add(new Packed(value, 0, 0));
                run--; i++;

                while (run >= 3) { int take = Math.Min(6, run); packed.Add(new Packed(16, take - 3, 2)); run -= take; i += take; }
                while (run-- > 0) { packed.Add(new Packed(value, 0, 0)); i++; }
            }

            return packed;
        }
    }
}
