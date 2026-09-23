using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Re2.Core.Formats;

/// <summary>
/// Encodes mono 16-bit PCM into the bank's ADPCM, producing a blob <see cref="SoundCodec.Decode"/>
/// reads back.
/// </summary>
public static class SoundEncoder
{
    private const int Q = 2048;             // Q11 one
    private const int SamplesPerRun = 30;   // nibble-coded samples following each run's two seeds
    private const int SeedsPerRun = 2;

    /// <summary>Encodes PCM, padding with silence to a whole number of frames.</summary>
    public static byte[] Encode(ReadOnlySpan<short> pcm)
    {
        int frames = Math.Max(1, (pcm.Length + SoundCodec.SamplesPerFrame - 1) / SoundCodec.SamplesPerFrame);

        // Pad so every frame is complete; the declared length is what trims playback.
        var padded = new short[frames * SoundCodec.SamplesPerFrame];
        pcm.CopyTo(padded);

        var (a1, a2) = BuildCodebook(padded);

        var blob = new byte[SoundCodec.BookBytes + frames * SoundCodec.FrameBytes];
        WriteCodebook(blob, a1, a2);

        for (int f = 0; f < frames; f++)
        {
            var frame = blob.AsSpan(SoundCodec.BookBytes + f * SoundCodec.FrameBytes, SoundCodec.FrameBytes);
            int at = f * SoundCodec.SamplesPerFrame;

            for (int run = 0; run < 2; run++)
            {
                int start = at + run * (SeedsPerRun + SamplesPerRun);
                short seed2 = padded[start];
                short seed1 = padded[start + 1];

                BinaryPrimitives.WriteInt16BigEndian(frame.Slice(run * 4, 2), seed2);
                BinaryPrimitives.WriteInt16BigEndian(frame.Slice(run * 4 + 2, 2), seed1);

                var target = padded.AsSpan(start + SeedsPerRun, SamplesPerRun);
                var (predictor, stored, nibbles) = ChooseBest(target, seed2, seed1, a1, a2);

                frame[8 + run * 16] = (byte)((predictor << 4) | stored);
                nibbles.CopyTo(frame.Slice(9 + run * 16, SoundCodec.NibbleBytesPerRun));
            }
        }

        return blob;
    }

    // ---- run encoding -----------------------------------------------------

    /// <summary>Tries every predictor and shift, returning the cheapest encoding of one run.</summary>
    private static (int Predictor, int StoredShift, byte[] Nibbles) ChooseBest(
        ReadOnlySpan<short> target, short seed2, short seed1, short[] a1, short[] a2)
    {
        long bestCost = long.MaxValue;
        int bestPredictor = 0, bestStored = SoundCodec.ShiftBias;
        var best = new byte[SoundCodec.NibbleBytesPerRun];
        var scratch = new byte[SoundCodec.NibbleBytesPerRun];

        for (int p = 0; p < SoundCodec.PredictorCount; p++)
        for (int stored = 0; stored <= SoundCodec.ShiftBias; stored++)
        {
            long cost = EncodeRun(target, seed2, seed1, a1[p], a2[p], SoundCodec.ShiftBias - stored, scratch);
            if (cost >= bestCost) continue;
            bestCost = cost;
            bestPredictor = p;
            bestStored = stored;
            scratch.CopyTo(best, 0);
        }

        return (bestPredictor, bestStored, best);
    }

    /// <summary>
    /// Encodes one run with a fixed predictor and shift, mirroring the decoder step for step so the
    /// quantiser sees exactly the state the decoder will have. Returns the squared error.
    /// </summary>
    private static long EncodeRun(ReadOnlySpan<short> target, short seed2, short seed1,
                                  int a1, int a2, int shift, Span<byte> nibbles)
    {
        int prev2 = seed2, prev1 = seed1;
        long cost = 0;
        int step = shift >= 0 ? 1 << shift : 1;

        for (int i = 0; i < target.Length; i += 2)
        {
            int packed = 0;
            for (int half = 0; half < 2; half++)
            {
                int predicted = (a1 * prev1 + a2 * prev2) >> 11;
                int error = target[i + half] - predicted;

                int nibble = shift >= 0
                    ? (int)Math.Round((double)error / step, MidpointRounding.AwayFromZero)
                    : error << -shift;
                nibble = Math.Clamp(nibble, -8, 7);

                int delta = shift >= 0 ? nibble << shift : nibble >> -shift;
                int y = Math.Clamp((a1 * prev1 + a2 * prev2 + (delta << 11)) >> 11, short.MinValue, short.MaxValue);

                long d = y - target[i + half];
                cost += d * d;

                packed |= (nibble & 0xF) << (half == 0 ? 4 : 0);
                prev2 = prev1;
                prev1 = y;
            }
            nibbles[i / 2] = (byte)packed;
        }

        return cost;
    }

    // ---- codebook ---------------------------------------------------------

    /// <summary>
    /// Fits an order-2 predictor to each frame-sized block, then reduces those fits to 8
    /// representatives by Lloyd iteration.
    /// </summary>
    private static (short[] A1, short[] A2) BuildCodebook(short[] pcm)
    {
        var fits = new List<(double A1, double A2)>();

        for (int at = 0; at + SoundCodec.SamplesPerFrame <= pcm.Length; at += SoundCodec.SamplesPerFrame)
        {
            var block = pcm.AsSpan(at, SoundCodec.SamplesPerFrame);
            if (TryFit(block, out double f1, out double f2)) fits.Add((f1, f2));
        }

        var a1 = new short[SoundCodec.PredictorCount];
        var a2 = new short[SoundCodec.PredictorCount];

        // Sensible defaults: no prediction, first difference, second difference, then spread.
        (double, double)[] seeds =
        {
            (0, 0), (1.0, 0), (2.0, -1.0), (1.5, -0.6),
            (1.8, -0.85), (0.5, 0), (1.2, -0.3), (1.9, -0.95)
        };
        var centres = new List<(double A1, double A2)>(seeds.Length);
        foreach (var s in seeds) centres.Add(s);

        if (fits.Count >= SoundCodec.PredictorCount)
        {
            for (int iteration = 0; iteration < 8; iteration++)
            {
                var sum = new (double A1, double A2, int N)[SoundCodec.PredictorCount];
                foreach (var f in fits)
                {
                    int best = 0; double bestD = double.MaxValue;
                    for (int c = 0; c < centres.Count; c++)
                    {
                        double d1 = f.A1 - centres[c].A1, d2 = f.A2 - centres[c].A2;
                        double d = d1 * d1 + d2 * d2;
                        if (d < bestD) { bestD = d; best = c; }
                    }
                    sum[best] = (sum[best].A1 + f.A1, sum[best].A2 + f.A2, sum[best].N + 1);
                }

                // Slot 0 stays pinned; the rest move to their cluster means.
                for (int c = 1; c < centres.Count; c++)
                    if (sum[c].N > 0) centres[c] = (sum[c].A1 / sum[c].N, sum[c].A2 / sum[c].N);
            }
        }

        for (int p = 0; p < SoundCodec.PredictorCount; p++)
        {
            var (c1, c2) = Stabilise(centres[p].A1, centres[p].A2);
            a1[p] = (short)Math.Clamp(Math.Round(c1 * Q), short.MinValue, short.MaxValue);
            a2[p] = (short)Math.Clamp(Math.Round(c2 * Q), short.MinValue, short.MaxValue);
        }

        return (a1, a2);
    }

    /// <summary>Least-squares order-2 fit over one block, via the 2x2 normal equations.</summary>
    private static bool TryFit(ReadOnlySpan<short> x, out double a1, out double a2)
    {
        double r11 = 0, r22 = 0, r12 = 0, y1 = 0, y2 = 0;
        for (int i = 2; i < x.Length; i++)
        {
            double p1 = x[i - 1], p2 = x[i - 2], t = x[i];
            r11 += p1 * p1; r22 += p2 * p2; r12 += p1 * p2;
            y1 += t * p1;   y2 += t * p2;
        }

        double det = r11 * r22 - r12 * r12;
        if (Math.Abs(det) < 1e-6 || r11 < 1e-6) { a1 = a2 = 0; return false; }

        a1 = (y1 * r22 - y2 * r12) / det;
        a2 = (y2 * r11 - y1 * r12) / det;
        return !double.IsNaN(a1) && !double.IsNaN(a2);
    }

    /// <summary>Keeps the pole pair inside the unit circle so the decoder cannot run away.</summary>
    private static (double A1, double A2) Stabilise(double a1, double a2)
    {
        a2 = Math.Clamp(a2, -0.995, 0.995);
        double limit = 1.0 - a2 - 1e-3;
        a1 = Math.Clamp(a1, -Math.Abs(limit) - 0.995, Math.Abs(limit) + 0.995);
        if (a1 + a2 >= 1.0) a1 = 1.0 - a2 - 1e-3;
        if (a2 - a1 >= 1.0) a1 = a2 - 1.0 + 1e-3;
        return (a1, a2);
    }

    /// <summary>
    /// Writes the 8 predictors as the Q11 impulse responses the decoder reads: row 2p is the
    /// response to a unit in state[-2], row 2p+1 the response to a unit in state[-1].
    /// </summary>
    private static void WriteCodebook(Span<byte> blob, short[] a1, short[] a2)
    {
        for (int p = 0; p < SoundCodec.PredictorCount; p++)
        {
            WriteRow(blob.Slice(p * 32, 16), a1[p], a2[p], prev2: Q, prev1: 0);
            WriteRow(blob.Slice(p * 32 + 16, 16), a1[p], a2[p], prev2: 0, prev1: Q);
        }
    }

    private static void WriteRow(Span<byte> row, int a1, int a2, int prev2, int prev1)
    {
        for (int i = 0; i < 8; i++)
        {
            int y = Math.Clamp((a1 * prev1 + a2 * prev2) >> 11, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16BigEndian(row.Slice(i * 2, 2), (short)y);
            prev2 = prev1;
            prev1 = y;
        }
    }
}
