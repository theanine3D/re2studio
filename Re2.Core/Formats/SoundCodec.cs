using System;
using System.Buffers.Binary;

namespace Re2.Core.Formats;

/// <summary>Decoder for the ADPCM the RE2 N64 sound bank stores its samples in.</summary>
public static class SoundCodec
{
    public const int BookBytes = 256;
    public const int BookRows = 16;
    public const int PredictorCount = 8;
    public const int FrameBytes = 40;
    public const int SamplesPerFrame = 64;
    public const int NibbleBytesPerRun = 15;

    /// <summary>The step is <c>1 &lt;&lt; (ShiftBias - stored)</c>, so the stored field caps at 12.</summary>
    public const int ShiftBias = 12;

    /// <summary>The order-2 predictor codebook that heads every sample.</summary>
    public sealed class Codebook
    {
        private readonly short[] _a1 = new short[PredictorCount];
        private readonly short[] _a2 = new short[PredictorCount];

        public Codebook(ReadOnlySpan<byte> data)
        {
            if (data.Length < BookBytes)
                throw new ArgumentException($"A codebook needs {BookBytes} bytes, got {data.Length}.");

            for (int p = 0; p < PredictorCount; p++)
            {
                _a2[p] = BinaryPrimitives.ReadInt16BigEndian(data.Slice(p * 32, 2));
                _a1[p] = BinaryPrimitives.ReadInt16BigEndian(data.Slice(p * 32 + 16, 2));
            }
        }

        public short A1(int predictor) => _a1[predictor];
        public short A2(int predictor) => _a2[predictor];
    }

    private static short Clamp16(int v) => v < -32768 ? (short)-32768 : v > 32767 ? (short)32767 : (short)v;

    /// <summary>Decodes one sample.</summary>
    public static short[] Decode(ReadOnlySpan<byte> stored, int sampleCount)
    {
        if (stored.Length < BookBytes + FrameBytes) return Array.Empty<short>();

        var book = new Codebook(stored);
        var body = stored[BookBytes..];
        int frames = body.Length / FrameBytes;

        var output = new short[frames * SamplesPerFrame];
        int w = 0;

        for (int f = 0; f < frames; f++)
        {
            var frame = body.Slice(f * FrameBytes, FrameBytes);

            for (int run = 0; run < 2; run++)
            {
                int header = frame[8 + run * 16];
                int predictor = header >> 4;
                int shift = ShiftBias - (header & 0xF);

                short prev2 = BinaryPrimitives.ReadInt16BigEndian(frame.Slice(run * 4, 2));
                short prev1 = BinaryPrimitives.ReadInt16BigEndian(frame.Slice(run * 4 + 2, 2));
                output[w++] = prev2;
                output[w++] = prev1;

                int a1 = book.A1(predictor), a2 = book.A2(predictor);
                var nibbles = frame.Slice(9 + run * 16, NibbleBytesPerRun);

                for (int i = 0; i < nibbles.Length; i++)
                {
                    int packed = nibbles[i];
                    for (int half = 0; half < 2; half++)
                    {
                        int nibble = half == 0 ? packed >> 4 : packed & 0xF;
                        if (nibble > 7) nibble -= 16;
                        int delta = shift >= 0 ? nibble << shift : nibble >> -shift;

                        short y = Clamp16((a1 * prev1 + a2 * prev2 + (delta << 11)) >> 11);
                        output[w++] = y;
                        prev2 = prev1;
                        prev1 = y;
                    }
                }
            }
        }

        if (sampleCount > 0 && sampleCount < output.Length) Array.Resize(ref output, sampleCount);
        return output;
    }

    /// <summary>How many samples a blob of this size holds, ignoring any declared length.</summary>
    public static int CapacityInSamples(int storedBytes)
        => storedBytes <= BookBytes ? 0 : (storedBytes - BookBytes) / FrameBytes * SamplesPerFrame;
}
