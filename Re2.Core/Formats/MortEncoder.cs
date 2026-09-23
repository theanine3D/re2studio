using System;
using System.Collections.Generic;
using System.Linq;

namespace Re2.Core.Formats;

/// <summary>Encodes PCM into the MORT speech codec.</summary>
public static class MortEncoder
{
    private const int Order = MortDecoder.Order;
    private const int BlockSamples = MortDecoder.SamplesPerBlock;
    private const int SubframeSamples = MortDecoder.SubframeSamples;

    /// <summary>The de-emphasis pole the decoder applies; analysis has to undo it.</summary>
    private const int Deemphasis = 0x6E14;

    /// <summary>Number of quantiser levels for each of the eight coefficient fields.</summary>
    private static readonly int[] FieldLevels = { 64, 64, 32, 32, 16, 16, 8, 8 };

    /// <summary>Which interpolation steps each subframe runs under.</summary>
    private static readonly int[][] SubframeSteps =
        { new[] { 0, 1, 2 }, new[] { 3 }, new[] { 3 }, new[] { 3 } };

    private static readonly int[][] SubframeLengths =
        { new[] { 13, 14, 13 }, new[] { 40 }, new[] { 40 }, new[] { 40 } };

    private static int Round15(int value) => (value + 0x4000) >> 15;

    /// <summary>The coefficient ramp from the previous frame's value to this frame's.</summary>
    internal static int Ramp(int current, int previous, int step) => step switch
    {
        0 => (current >> 2) + (previous >> 1) + (previous >> 2),
        1 => (current >> 1) + (previous >> 1),
        2 => (current >> 1) + (current >> 2) + (previous >> 2),
        _ => current,
    };

    // ---- coefficient quantisation ----------------------------------------

    /// <summary>
    /// Undoes the decoder's companding, so a wanted reflection coefficient becomes the stored value
    /// that produces it. Piecewise linear in the same three pieces, inverted.
    /// </summary>
    public static int Expand(int companded)
    {
        bool negative = companded < 0;
        int y = negative ? -companded : companded;

        int x = y < 0x5666 ? y / 2
              : y < 0x7999 ? y - 0x2B33
              : (y - 0x6600) * 4;

        return negative ? -x : x;
    }

    /// <summary>The field value whose dequantisation lands nearest <paramref name="wanted"/>.</summary>
    public static int QuantiseField(int field, int wanted)
    {
        var probe = new int[Order];
        Span<short> decoded = stackalloc short[Order];

        int best = 0, bestError = int.MaxValue;

        for (int level = 0; level < FieldLevels[field]; level++)
        {
            probe[field] = level;
            MortDecoder.Dequantise(probe, decoded);

            int error = Math.Abs(decoded[field] - wanted);
            if (error >= bestError) continue;

            bestError = error;
            best = level;
        }

        return best;
    }

    // ---- linear prediction -----------------------------------------------

    /// <summary>
    /// Reflection coefficients for one frame, by autocorrelation and Levinson-Durbin.
    /// </summary>
    public static double[] Reflection(ReadOnlySpan<double> frame)
    {
        var windowed = new double[frame.Length];
        for (int i = 0; i < frame.Length; i++)
            windowed[i] = frame[i] * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (frame.Length - 1)));

        var r = new double[Order + 1];
        for (int lag = 0; lag <= Order; lag++)
        {
            double sum = 0;
            for (int i = lag; i < windowed.Length; i++) sum += windowed[i] * windowed[i - lag];
            r[lag] = sum;
        }

        var k = new double[Order];
        if (r[0] <= 0) return k;

        r[0] = r[0] * 1.0001 + 1.0;

        var a = new double[Order + 1];
        double error = r[0];

        for (int m = 1; m <= Order; m++)
        {
            double acc = r[m];
            for (int i = 1; i < m; i++) acc -= a[i] * r[m - i];

            double reflection = error == 0 ? 0 : acc / error;
            reflection = Math.Clamp(reflection, -0.995, 0.995);
            k[m - 1] = reflection;

            var previous = (double[])a.Clone();
            a[m] = reflection;
            for (int i = 1; i < m; i++) a[i] = previous[i] - reflection * previous[m - i];

            error *= 1 - reflection * reflection;
            if (error <= 0) break;
        }

        return k;
    }

    // ---- the analysis lattice --------------------------------------------

    /// <summary>
    /// The exact inverse of the decoder's lattice: given the signal wanted out, the excitation that
    /// would produce it.
    /// </summary>
    public sealed class AnalysisLattice
    {
        private readonly int[] _b = new int[Order];
        private static readonly int[] StepLengths = { 13, 14, 13, 120 };

        public void Run(short[] current, short[] previous, ReadOnlySpan<int> signal, Span<int> excitation)
        {
            Span<int> k = stackalloc int[Order];
            int at = 0;

            for (int step = 0; step < StepLengths.Length; step++)
            {
                for (int j = 0; j < Order; j++)
                    k[j] = MortDecoder.Compand(Ramp(current[Order - 1 - j], previous[Order - 1 - j], step));

                for (int n = 0; n < StepLengths[step]; n++, at++)
                {
                    int u = signal[at];
                    int b0 = u;

                    // The deepest backward value is consumed last but the loop overwrites it, so it
                    // is taken first -- synthesis reads it before updating anything.
                    int deepest = _b[Order - 1];

                    for (int j = Order - 1; j >= 1; j--)
                    {
                        int coefficient = k[j];
                        int below = _b[Order - 1 - j];
                        int next = u + Round15(below * coefficient);
                        _b[Order - j] = Round15(coefficient * u) + below;
                        u = next;
                    }

                    u += Round15(deepest * k[0]);
                    _b[0] = b0;
                    excitation[at] = u;
                }
            }
        }
    }

    // ---- closed-loop search ----------------------------------------------

    /// <summary>The synthesis lattice over one subframe, from a copy of the filter state.</summary>
    private static void RunLattice(int[] backward, short[] current, short[] previous,
                                   int subframe, ReadOnlySpan<short> excitation, Span<int> signal)
    {
        var steps = SubframeSteps[subframe];
        var lengths = SubframeLengths[subframe];

        Span<int> k = stackalloc int[Order];
        int at = 0;

        for (int step = 0; step < steps.Length; step++)
        {
            for (int j = 0; j < Order; j++)
                k[j] = MortDecoder.Compand(Ramp(current[Order - 1 - j], previous[Order - 1 - j], steps[step]));

            for (int n = 0; n < lengths[step]; n++, at++)
            {
                int f = excitation[at];
                f -= Round15(backward[7] * k[0]);

                for (int j = 1; j < Order; j++)
                {
                    int coefficient = k[j];
                    int below = backward[Order - 1 - j];
                    f -= Round15(below * coefficient);
                    backward[Order - j] = Round15(coefficient * f) + below;
                }

                backward[0] = f;
                signal[at] = f;
            }
        }
    }

    /// <summary>Squared error between what a candidate excitation produces and what is wanted.</summary>
    private static double Cost(MortDecoder.State state, int[] filter, int subframe,
                               ReadOnlySpan<short> excitation,
                               ReadOnlySpan<int> wanted, int[] scratch, int[] signal)
    {
        filter.CopyTo(scratch, 0);
        RunLattice(scratch, state.Current, state.Previous, subframe, excitation, signal);

        double error = 0;
        for (int i = 0; i < SubframeSamples; i++)
        {
            double difference = (double)signal[i] - wanted[i];
            error += difference * difference;
        }

        return error;
    }

    /// <summary>
    /// Picks one subframe's parameters: the pitch lag and gain whose filtered contribution best matches
    /// the wanted audio, then the pulse pattern that best covers what is left.
    /// </summary>
    private static MortCodec.Group ChooseSubframe(MortDecoder.State state, int[] filter, int subframe,
                                                  ReadOnlySpan<int> wanted, ReadOnlySpan<int> target)
    {
        var history = state.History;
        var scratch = new int[Order];
        var signal = new int[SubframeSamples];
        var excitation = new short[SubframeSamples];

        int bestLag = MortDecoder.MinLag, bestGainIndex = 0;
        double bestError = double.MaxValue;

        // The decoder shifts its history left by a subframe before predicting from it, so what it reads
        // at (120 - lag + i) of the shifted buffer is (160 - lag + i) of the buffer as it stands here.
        for (int lag = MortDecoder.MinLag; lag <= MortDecoder.MaxLag; lag++)
        {
            int from = BlockSamples - lag;

            for (int gainIndex = 0; gainIndex < MortDecoder.PitchGains.Length; gainIndex++)
            {
                int gain = MortDecoder.PitchGains[gainIndex];
                for (int i = 0; i < SubframeSamples; i++)
                    excitation[i] = (short)Round15(history[from + i] * gain);

                double error = Cost(state, filter, subframe, excitation, wanted, scratch, signal);
                if (error >= bestError) continue;

                bestError = error;
                bestLag = lag;
                bestGainIndex = gainIndex;
            }
        }

        var predicted = new short[SubframeSamples];
        int predictedFrom = BlockSamples - bestLag;
        int pitchGain = MortDecoder.PitchGains[bestGainIndex];

        for (int i = 0; i < SubframeSamples; i++)
            predicted[i] = (short)Round15(history[predictedFrom + i] * pitchGain);

        // What the pitch predictor could not supply is what the pulses have to.
        var residual = new int[SubframeSamples];
        for (int i = 0; i < SubframeSamples; i++) residual[i] = target[i] - predicted[i];

        int bestPhase = 0, bestCode = 0;
        var bestPulses = new int[MortDecoder.PulseCount];
        bestError = double.MaxValue;

        var candidate = new int[MortDecoder.PulseCount];
        var pulses = new short[SubframeSamples];
        var levels = new int[8];
        var probe = new int[MortDecoder.PulseCount];

        for (int phase = 0; phase < 4; phase++)
        {
            for (int code = 0; code < 64; code++)
            {
                // The eight amplitudes this gain can express, in the decoder's own arithmetic.
                for (int c = 0; c < 8; c++)
                {
                    Array.Clear(probe);
                    probe[0] = c;
                    MortDecoder.BuildPulses(code, 0, probe, pulses);
                    levels[c] = pulses[0];
                }

                // Nearest level per position: the positions are independent, so for a given gain
                // this is the best pattern without a search of its own.
                for (int pulse = 0; pulse < MortDecoder.PulseCount; pulse++)
                {
                    int want = residual[phase + pulse * MortDecoder.PulseStride];
                    int pick = 0, pickError = int.MaxValue;

                    for (int c = 0; c < 8; c++)
                    {
                        int difference = Math.Abs(levels[c] - want);
                        if (difference >= pickError) continue;
                        pickError = difference;
                        pick = c;
                    }

                    candidate[pulse] = pick;
                }

                MortDecoder.BuildPulses(code, phase, candidate, pulses);
                for (int i = 0; i < SubframeSamples; i++)
                    excitation[i] = (short)(predicted[i] + pulses[i]);

                double error = Cost(state, filter, subframe, excitation, wanted, scratch, signal);
                if (error >= bestError) continue;

                bestError = error;
                bestPhase = phase;
                bestCode = code;
                candidate.CopyTo(bestPulses, 0);
            }
        }

        // Carry the filter past this subframe on what was actually chosen.
        MortDecoder.BuildPulses(bestCode, bestPhase, bestPulses, pulses);
        for (int i = 0; i < SubframeSamples; i++)
            excitation[i] = (short)(predicted[i] + pulses[i]);

        RunLattice(filter, state.Current, state.Previous, subframe, excitation, signal);

        return new MortCodec.Group(new[] { bestLag, bestGainIndex, bestPhase, bestCode }, bestPulses);
    }

    // ---- the encoder -----------------------------------------------------

    /// <summary>How the encode went, for the import log.</summary>
    public sealed record Report(int Blocks, int SilentBlocks, double SignalToNoise);

    /// <summary>Mean squared amplitude below which a block is sent as silence, by default.</summary>
    public const long DefaultSilenceEnergy = 900;

    /// <summary>Encodes <paramref name="pcm"/> into a complete clip, header and all.</summary>
    public static byte[] Encode(ReadOnlySpan<short> pcm, int sampleRate, out Report report,
                                long silenceEnergy = DefaultSilenceEnergy, bool[]? forceSilent = null)
    {
        int blocks = Math.Max(1, (pcm.Length + BlockSamples - 1) / BlockSamples);

        var padded = new short[blocks * BlockSamples];
        pcm.CopyTo(padded);

        var state = new MortDecoder.State();
        var analysis = new AnalysisLattice();
        var entries = new List<MortCodec.Block?>(blocks);

        // The signal the lattice itself has to produce: half the sample, pre-emphasised, which is
        // what the decoder's doubling and de-emphasis undo on the way out.
        var wanted = new int[padded.Length];
        int previousHalf = 0;
        for (int i = 0; i < padded.Length; i++)
        {
            int half = padded[i] >> 1;
            wanted[i] = half - Round15(Deemphasis * previousHalf);
            previousHalf = half;
        }

        var reconstructed = new short[padded.Length];

        int silent = 0;

        // Reused by every block; BuildPulses clears it before each subframe.
        Span<short> excitation = stackalloc short[SubframeSamples];

        for (int block = 0; block < blocks; block++)
        {
            int at = block * BlockSamples;

            // Quiet blocks are sent as silence: five bits instead of 260, and the decoder clears
            // the output without touching its filters, which is exactly what a gap between phrases
            // should do. The threshold is deliberately low -- anything audible is worth coding.
            long energy = 0;
            for (int i = 0; i < BlockSamples; i++) energy += (long)padded[at + i] * padded[at + i];

            bool quiet = forceSilent is not null ? forceSilent[block]
                                                 : energy / BlockSamples < silenceEnergy;

            if (quiet)
            {
                entries.Add(null);
                silent++;
                continue;
            }

            var frame = new double[BlockSamples];
            for (int i = 0; i < BlockSamples; i++) frame[i] = wanted[at + i];

            var reflection = Reflection(frame);
            var scales = new int[Order];

            for (int j = 0; j < Order; j++)
            {
                int companded = (int)Math.Round(Math.Clamp(reflection[j], -0.999, 0.999) * 32767);
                scales[j] = QuantiseField(j, Expand(companded));
            }

            MortDecoder.Dequantise(scales, state.Current);

            var target = new int[BlockSamples];
            analysis.Run(state.Current, state.Previous, wanted.AsSpan(at, BlockSamples), target);

            var groups = new MortCodec.Group[MortDecoder.SubframeCount];

            // A working copy of the lattice, advanced subframe by subframe as choices are made.
            var filter = (int[])state.Lattice.Clone();

            for (int g = 0; g < MortDecoder.SubframeCount; g++)
            {
                groups[g] = ChooseSubframe(state, filter, g,
                                           wanted.AsSpan(at + g * SubframeSamples, SubframeSamples),
                                           target.AsSpan(g * SubframeSamples, SubframeSamples));

                MortDecoder.BuildPulses(groups[g].Side[3], groups[g].Side[2], groups[g].Coefficients, excitation);
                MortDecoder.ApplyPitch(state, groups[g].Side[0], groups[g].Side[1], excitation);
            }

            MortDecoder.Synthesise(state, state.History, reconstructed.AsSpan(at, BlockSamples));
            state.Retire();

            entries.Add(new MortCodec.Block(scales, groups));
        }

        report = new Report(blocks, silent, SignalToNoise(padded, reconstructed));
        return MortCodec.WriteMixed(entries, sampleRate);
    }

    /// <summary>Encodes to fit a slot of a fixed size and duration.</summary>
    public static byte[] EncodeToFit(ReadOnlySpan<short> pcm, int sampleRate, int maxBytes,
                                     int blocks, out Report report, out string note)
    {
        // The slot's duration is fixed, so the audio is cut or padded to it before anything else.
        var fitted = new short[blocks * BlockSamples];
        pcm[..Math.Min(pcm.Length, fitted.Length)].CopyTo(fitted);

        note = pcm.Length > fitted.Length
            ? $"source is longer than the slot: {(pcm.Length - fitted.Length) / (double)sampleRate:0.00}s trimmed. "
            : "";

        // How big a clip comes out is settled entirely by which blocks are silent -- a normal block is
        // always 260 bits and a silent one five.
        var energy = new long[blocks];
        for (int block = 0; block < blocks; block++)
        {
            long sum = 0;
            for (int i = 0; i < BlockSamples; i++)
            {
                long sample = fitted[block * BlockSamples + i];
                sum += sample * sample;
            }
            energy[block] = sum / BlockSamples;
        }

        var quietestFirst = Enumerable.Range(0, blocks).OrderBy(b => energy[b]).ToArray();

        // The quietest blocks go first, so what is dropped is what is least missed.
        int drop = 0;
        while (drop <= blocks && PredictBytes(Mask(blocks, quietestFirst, drop)) > maxBytes) drop++;

        // Anything already below the ordinary threshold is silent regardless of the budget.
        int free = quietestFirst.Count(b => energy[b] < DefaultSilenceEnergy);
        drop = Math.Max(drop, free);

        if (drop > free)
            note += $"the slot's {maxBytes:N0} bytes needed {drop - free:N0} more block(s) dropped to " +
                    "silence; the quietest went first. ";

        return Encode(fitted, sampleRate, out report, DefaultSilenceEnergy,
                      Mask(blocks, quietestFirst, drop));
    }

    private static bool[] Mask(int blocks, int[] quietestFirst, int drop)
    {
        var mask = new bool[blocks];
        for (int i = 0; i < drop && i < blocks; i++) mask[quietestFirst[i]] = true;
        return mask;
    }

    /// <summary>
    /// The size a clip with this silence pattern will come to, counted the way
    /// <see cref="MortCodec.WriteMixed"/> packs its runs.
    /// </summary>
    private static int PredictBytes(bool[] silent)
    {
        int bits = MortCodec.FirstBitPosition;

        for (int at = 0; at < silent.Length;)
        {
            bool quiet = silent[at];
            int limit = quiet ? MortCodec.MaxSilentRun : MortCodec.MaxNormalRun;

            int run = 0;
            while (at + run < silent.Length && run < limit && silent[at + run] == quiet) run++;

            bits += MortCodec.SelectorBits + (quiet ? MortCodec.SilentRunBits : MortCodec.NormalRunBits);
            if (!quiet) bits += run * MortCodec.NormalBlockBits;

            at += run;
        }

        return Math.Max((bits + 31) / 32 * 4, Assets.VoiceBank.HeaderSize);
    }

    private static double SignalToNoise(short[] original, short[] decoded)
    {
        double signal = 0, noise = 0;

        for (int i = 0; i < original.Length; i++)
        {
            double difference = original[i] - decoded[i];
            signal += (double)original[i] * original[i];
            noise += difference * difference;
        }

        return noise <= 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / Math.Max(1e-9, noise));
    }
}
