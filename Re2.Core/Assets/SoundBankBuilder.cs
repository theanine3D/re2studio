using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Formats;

namespace Re2.Core.Assets;

/// <summary>
/// Rebuilds the two assets that make up the sample bank -- the directory (1982) and the packed sample
/// data (1983) -- with some samples replaced.
/// </summary>
public static class SoundBankBuilder
{
    /// <summary>New audio for one sample, as PCM at <paramref name="SampleRate"/>.</summary>
    public sealed record Replacement(int Index, short[] Pcm, int SampleRate);

    /// <summary>
    /// The highest rate any retail sample uses -- measured across all 1,192 of them, not assumed.
    /// </summary>
    public const int HighestRetailRate = 38000;

    /// <summary>Produces the new (directory, sampleData) pair.</summary>
    public static (byte[] Directory, byte[] SampleData) Rebuild(
        SoundDirectory bank, IReadOnlyList<Replacement> replacements, Action<string>? report = null,
        SoundDirectory? cartBank = null)
    {
        var byIndex = replacements.ToDictionary(r => r.Index);

        // The table ends with an FFFFFFFF terminator past the last record, which the game presumably
        // scans for. Dropping it is invisible until a rebuild fails to match byte for byte.
        var table = new byte[bank.Samples.Count * SoundDirectory.RecordSize + SoundDirectory.TerminatorSize];
        table.AsSpan(bank.Samples.Count * SoundDirectory.RecordSize).Fill(0xFF);
        var data = new List<byte>(bank.SampleData.Length);

        for (int i = 0; i < bank.Samples.Count; i++)
        {
            var sample = bank.Samples[i];
            int offset = data.Count;

            byte[] encoded;
            int rate, declared;
            uint loopStart = sample.LoopStart, loopEnd = sample.LoopEnd;

            if (byIndex.TryGetValue(sample.Index, out var swap))
            {
                // The rate a record declares is what the console plays the sample at, and every rate in
                // the cart is one its audio driver was built around -- 9,800 to 38,000 Hz.
                var pcm = swap.Pcm;

                // The game's own rate for this sample, never the rate a previous import left behind.
                int original = cartBank?.Samples.FirstOrDefault(s => s.Index == sample.Index)?.SampleRate
                               ?? sample.SampleRate;

                int target = original > 0 ? original : Math.Min(swap.SampleRate, HighestRetailRate);

                if (swap.SampleRate != target && swap.SampleRate > 0 && pcm.Length > 0)
                {
                    pcm = Resample(pcm, swap.SampleRate, target);
                    report?.Invoke($"sample {sample.Index}: resampled {swap.SampleRate:N0} Hz -> {target:N0} Hz " +
                                   $"({swap.Pcm.Length:N0} -> {pcm.Length:N0} samples).");
                }

                encoded = SoundEncoder.Encode(pcm);
                rate = target;
                declared = pcm.Length;

                // A shorter replacement would leave the loop pointing past the end.
                if (loopEnd > declared) { loopStart = 0; loopEnd = 0; }
            }
            else
            {
                encoded = bank.GetEncoded(sample).ToArray();
                rate = sample.SampleRate;
                declared = (int)sample.DeclaredLength;
            }

            data.AddRange(encoded);
            WriteRecord(table.AsSpan(i * SoundDirectory.RecordSize), sample.Index, offset, rate,
                        declared, loopStart, loopEnd);
        }

        return (table, data.ToArray());
    }

    /// <summary>Rate conversion.</summary>
    public static short[] Resample(short[] pcm, int from, int to)
    {
        if (from == to || pcm.Length == 0) return pcm;

        int length = (int)Math.Max(1, (long)pcm.Length * to / from);
        var output = new short[length];
        double step = (double)from / to;

        for (int i = 0; i < length; i++)
        {
            double start = i * step;
            double end = start + step;

            if (step > 1)
            {
                // Downsampling: the mean of every input sample the output sample covers.
                int first = (int)start;
                int last = Math.Min(pcm.Length - 1, (int)Math.Ceiling(end) - 1);
                long sum = 0;
                for (int j = first; j <= last; j++) sum += pcm[j];
                output[i] = (short)Math.Clamp(sum / Math.Max(1, last - first + 1), short.MinValue, short.MaxValue);
            }
            else
            {
                int j = (int)start;
                double frac = start - j;
                short a = pcm[Math.Min(j, pcm.Length - 1)];
                short b = pcm[Math.Min(j + 1, pcm.Length - 1)];
                output[i] = (short)Math.Clamp(a + (b - a) * frac, short.MinValue, short.MaxValue);
            }
        }

        return output;
    }

    /// <summary>Writes one 28-byte record.</summary>
    private static void WriteRecord(Span<byte> record, int index, int offset, int rate,
                                    int declaredLength, uint loopStart, uint loopEnd)
    {
        BinaryPrimitives.WriteUInt32BigEndian(record, (uint)index << 16);
        BinaryPrimitives.WriteUInt32BigEndian(record[4..], (uint)offset);
        BinaryPrimitives.WriteUInt32BigEndian(record[8..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(record[12..], ((uint)SoundDirectory.RateWordMarker << 16) | (uint)(rate & 0xFFFF));
        BinaryPrimitives.WriteUInt32BigEndian(record[16..], (SoundDirectory.LengthWordMarker << 24) | (uint)(declaredLength & 0x00FFFFFF));
        BinaryPrimitives.WriteUInt32BigEndian(record[20..], loopStart);
        BinaryPrimitives.WriteUInt32BigEndian(record[24..], loopEnd);
    }
}
