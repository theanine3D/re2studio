using System;
using System.Collections.Generic;

namespace Re2.Core.Formats;

/// <summary>
/// Parses and decodes a whole MORT clip without an interpreter, using <see cref="MortDecoder"/>.
/// </summary>
public static class MortStream
{
    /// <summary>One parsed block: either silence, or a full set of fields.</summary>
    public sealed record Entry(MortCodec.Block? Block)
    {
        public bool IsSilent => Block is null;
    }

    /// <summary>Reads every block's fields, stopping after <paramref name="blockCount"/> blocks.</summary>
    public static List<Entry> Parse(ReadOnlySpan<byte> clip, int blockCount, out int endBitPosition)
    {
        var reader = new MortBitReader(clip, MortCodec.FirstBitPosition);
        var entries = new List<Entry>(blockCount);

        while (entries.Count < blockCount)
        {
            if (reader.Read(MortCodec.SelectorBits) != 0)
            {
                int run = reader.Read(MortCodec.SilentRunBits) + 1;
                for (int i = 0; i < run && entries.Count < blockCount; i++) entries.Add(new Entry(null));
                continue;
            }

            int normal = reader.Read(MortCodec.NormalRunBits) + 1;

            for (int i = 0; i < normal && entries.Count < blockCount; i++)
            {
                var scales = new int[MortCodec.ScaleFieldBits.Length];
                for (int f = 0; f < scales.Length; f++) scales[f] = reader.Read(MortCodec.ScaleFieldBits[f]);

                var groups = new MortCodec.Group[MortCodec.GroupsPerBlock];
                for (int g = 0; g < groups.Length; g++)
                {
                    var side = new int[MortCodec.GroupSideBits.Length];
                    for (int f = 0; f < side.Length; f++) side[f] = reader.Read(MortCodec.GroupSideBits[f]);

                    var pulses = new int[MortCodec.CoefficientsPerGroup];
                    for (int c = 0; c < pulses.Length; c++) pulses[c] = reader.Read(MortCodec.CoefficientBits);

                    groups[g] = new MortCodec.Group(side, pulses);
                }

                entries.Add(new Entry(new MortCodec.Block(scales, groups)));
            }
        }

        endBitPosition = reader.Position;
        return entries;
    }

    /// <summary>Decodes a clip to PCM, the same samples the console produces.</summary>
    public static short[] Decode(ReadOnlySpan<byte> clip, int blockCount)
    {
        var entries = Parse(clip, blockCount, out _);
        var samples = new short[blockCount * MortDecoder.SamplesPerBlock];
        var state = new MortDecoder.State();

        for (int i = 0; i < entries.Count; i++)
        {
            var output = samples.AsSpan(i * MortDecoder.SamplesPerBlock, MortDecoder.SamplesPerBlock);

            // A silent block only clears its output: the decoder leaves its filters and history
            // exactly as they were, which is why the sound resumes rather than restarts after one.
            if (entries[i].IsSilent) continue;

            MortDecoder.DecodeBlock(state, entries[i].Block!, output);
        }

        return samples;
    }
}
