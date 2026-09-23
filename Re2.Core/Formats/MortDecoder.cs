using System;

namespace Re2.Core.Formats;

/// <summary>The MORT speech codec, reimplemented from the game's own decoder.</summary>
public static class MortDecoder
{
    public const int SamplesPerBlock = 160;
    public const int SubframeCount = 4;
    public const int SubframeSamples = 40;
    public const int Order = 8;

    /// <summary>Pitch gains, indexed by the 2-bit field. Q15: 0.1, 0.35, 0.65, 1.0.</summary>
    public static readonly int[] PitchGains = { 0x0CCD, 0x2CCD, 0x5333, 0x7FFF };

    /// <summary>Exponent/mantissa pairs for pulse-gain codes 0..7, which escape the regular split.</summary>
    private static readonly int[] SmallGain = { 0xC7, 0xD7, 0xE3, 0xE7, 0xF1, 0xF3, 0xF5, 0xF7 };

    /// <summary>Pitch lags outside this range leave the previous subframe's lag in place.</summary>
    public const int MinLag = 40, MaxLag = 120;

    /// <summary>Pulses per subframe, and the grid they sit on.</summary>
    public const int PulseCount = 13, PulseStride = 3;

    /// <summary>The decoder's state between blocks: excitation history, lattice memory, lag.</summary>
    public sealed class State
    {
        /// <summary>Excitation, oldest first. The newest 40 samples land at 120.</summary>
        public readonly short[] History = new short[SamplesPerBlock];

        /// <summary>Lattice backward values, and the de-emphasis accumulator.</summary>
        public readonly int[] Lattice = new int[Order];
        public int Deemphasis;

        /// <summary>This frame's reflection coefficients and the previous frame's.</summary>
        public readonly short[] Current = new short[Order];
        public readonly short[] Previous = new short[Order];

        public int Lag = MinLag;

        /// <summary>Retires the current coefficients, as the decoder's parity flag does.</summary>
        public void Retire()
        {
            for (int i = 0; i < Order; i++) Previous[i] = Current[i];
        }
    }

    private static int Round15(int value) => (value + 0x4000) >> 15;

    /// <summary>Dequantises the eight coefficient fields.</summary>
    public static void Dequantise(ReadOnlySpan<int> fields, Span<short> coefficients)
    {
        coefficients[0] = (short)(Round15((fields[0] - 0x20) * 0xCCCC00) << 1);
        coefficients[1] = (short)(Round15((fields[1] - 0x20) * 0xCCCC00) << 1);
        coefficients[2] = (short)(Round15((fields[2] * 0x400 - 0x5000) * 0x3333) << 1);
        coefficients[3] = (short)(Round15((fields[3] * 0x400 - 0x2C00) * 0x3333) << 1);
        coefficients[4] = (short)(Round15((fields[4] * 0x400 - 0x20BC) * 0x4B17) << 1);
        coefficients[5] = (short)(Round15((fields[5] * 0x400 - 0x1200) * 0x4444) << 1);
        coefficients[6] = (short)(Round15(((fields[6] - 4) * 0x400 + 0x2AA) * 0x7ADE) << 1);
        coefficients[7] = (short)(Round15((fields[7] * 0x400 - 0x710) * 0x740C) << 1);
    }

    /// <summary>
    /// The companding applied to an interpolated coefficient on its way into the lattice: piecewise
    /// linear in three pieces, continuous at both knees, giving finer resolution near the extremes
    /// where a reflection coefficient decides stability.
    /// </summary>
    public static int Compand(int value)
    {
        bool negative = value < 0;
        int x = negative ? -value : value;

        int y = x * 2;
        if (x >= 0x2B33) y = x + 0x2B33;
        if (x >= 0x4E66) y = (x >> 2) + 0x6600;
        if (y > 0x7FFF) y = 0x7FFF;

        return (short)(negative ? -y : y);
    }

    /// <summary>The four interpolation weights: a quarter of the way to this frame, then on to all.</summary>
    private static int Interpolate(int current, int previous, int step) => step switch
    {
        0 => (current >> 2) + (previous >> 1) + (previous >> 2),
        1 => (current >> 1) + (previous >> 1),
        2 => (current >> 1) + (current >> 2) + (previous >> 2),
        _ => current,
    };

    /// <summary>Samples filtered under each interpolation step; the last holds for the rest.</summary>
    private static readonly int[] StepLengths = { 13, 14, 13, 120 };

    /// <summary>Expands one subframe's pulse pattern into 40 samples of excitation.</summary>
    public static void BuildPulses(int gainCode, int phase, ReadOnlySpan<int> pulses, Span<short> excitation)
    {
        Split(gainCode, out int exponent, out int mantissa);

        int scale = mantissa * 0x800 + 0x47FF;
        excitation.Clear();

        for (int i = 0; i < PulseCount; i++)
        {
            // The three-bit code is a signed level: 0..7 becomes -7,-5,-3,-1,1,3,5,7 in 1/4096ths.
            int value = Round15((pulses[i] * 0x2000 - 0x7000) * scale);

            // Then scaled by the exponent, with the rounding the game uses.
            value = (value + (1 << (5 - exponent))) >> (6 - exponent);
            excitation[phase + i * PulseStride] = (short)value;
        }
    }

    /// <summary>The exponent and mantissa a six-bit pulse-gain code stands for.</summary>
    public static void Split(int gainCode, out int exponent, out int mantissa)
    {
        if (gainCode <= 7)
        {
            int packed = (sbyte)SmallGain[gainCode];
            exponent = packed >> 4;
            mantissa = packed & 7;
        }
        else
        {
            exponent = (gainCode - 8) >> 3;
            mantissa = gainCode & 7;
        }
    }

    /// <summary>Applies the adaptive codebook and appends the subframe to the excitation history.</summary>
    public static void ApplyPitch(State state, int lagField, int gainIndex, ReadOnlySpan<short> excitation)
    {
        if (lagField >= MinLag && lagField <= MaxLag) state.Lag = lagField;
        int gain = PitchGains[gainIndex];

        var history = state.History;
        const int kept = SamplesPerBlock - SubframeSamples;

        for (int i = 0; i < kept; i++) history[i] = history[i + SubframeSamples];

        int from = kept - state.Lag;
        for (int i = 0; i < SubframeSamples; i++)
            history[kept + i] = (short)(Round15(history[from + i] * gain) + excitation[i]);
    }

    /// <summary>
    /// The eighth-order all-pole lattice, plus de-emphasis and the output's 13-bit quantisation.
    /// </summary>
    public static void Synthesise(State state, ReadOnlySpan<short> excitation, Span<short> output)
    {
        Span<int> k = stackalloc int[Order];
        var b = state.Lattice;
        int at = 0;

        for (int step = 0; step < StepLengths.Length; step++)
        {
            // Coefficients enter the lattice deepest-first, which is why this runs the array down.
            for (int j = 0; j < Order; j++)
                k[j] = Compand(Interpolate(state.Current[Order - 1 - j], state.Previous[Order - 1 - j], step));

            for (int n = 0; n < StepLengths[step]; n++, at++)
            {
                int f = excitation[at];

                // The deepest stage feeds the forward value, but its own backward value is never
                // read again, so the game does not compute it.
                f -= Round15(b[7] * k[0]);

                for (int j = 1; j < Order; j++)
                {
                    int coefficient = k[j];
                    int below = b[Order - 1 - j];
                    f -= Round15(below * coefficient);
                    b[Order - j] = Round15(coefficient * f) + below;
                }

                b[0] = f;
                state.Deemphasis = f + Round15(0x6E14 * state.Deemphasis);

                int doubled = state.Deemphasis * 2;
                int overflow = ((doubled >> 15) + 1) >> 1;
                if (overflow != 0) doubled = overflow < 0 ? -0x8000 : 0x7FFF;

                output[at] = (short)(doubled & 0xFFF8);
            }
        }
    }

    /// <summary>Decodes one normal block from its already-parsed fields.</summary>
    public static void DecodeBlock(State state, MortCodec.Block block, Span<short> output)
    {
        Dequantise(block.Scales, state.Current);

        Span<short> excitation = stackalloc short[SubframeSamples];

        for (int g = 0; g < SubframeCount; g++)
        {
            var group = block.Groups[g];
            BuildPulses(group.Side[3], group.Side[2], group.Coefficients, excitation);
            ApplyPitch(state, group.Side[0], group.Side[1], excitation);
        }

        Synthesise(state, state.History, output);
        state.Retire();
    }
}
