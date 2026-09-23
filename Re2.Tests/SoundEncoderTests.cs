using System;
using System.Buffers.Binary;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

public sealed class SoundEncoderTests
{
    private readonly ITestOutputHelper _output;
    public SoundEncoderTests(ITestOutputHelper output) => _output = output;

    private static double SignalToNoiseDb(ReadOnlySpan<short> reference, ReadOnlySpan<short> actual)
    {
        int n = Math.Min(reference.Length, actual.Length);
        double signal = 0, noise = 0;
        for (int i = 0; i < n; i++)
        {
            double d = actual[i] - reference[i];
            signal += (double)reference[i] * reference[i];
            noise += d * d;
        }
        if (noise <= 0) return double.PositiveInfinity;
        return 10 * Math.Log10(signal / noise);
    }

    [Fact]
    public void EncodedBlobHasTheContainerShape()
    {
        var pcm = new short[1000];
        var blob = SoundEncoder.Encode(pcm);

        Assert.True(blob.Length > SoundCodec.BookBytes);
        Assert.Equal(0, (blob.Length - SoundCodec.BookBytes) % SoundCodec.FrameBytes);
        Assert.True(SoundCodec.CapacityInSamples(blob.Length) >= pcm.Length);
    }

    /// <summary>
    /// Each run stores its first two samples verbatim, so those must survive exactly.
    /// </summary>
    [Fact]
    public void RunSeedsAreStoredExactly()
    {
        var rng = new Random(7);
        var pcm = Enumerable.Range(0, SoundCodec.SamplesPerFrame * 4)
            .Select(_ => (short)rng.Next(short.MinValue, short.MaxValue))
            .ToArray();

        var blob = SoundEncoder.Encode(pcm);
        var decoded = SoundCodec.Decode(blob, pcm.Length);

        for (int i = 0; i < pcm.Length; i++)
        {
            int inFrame = i % SoundCodec.SamplesPerFrame;
            bool isSeed = inFrame is 0 or 1 or 32 or 33;
            if (isSeed) Assert.Equal(pcm[i], decoded[i]);
        }
    }

    [Fact]
    public void SineRoundTripsCleanly()
    {
        var pcm = Enumerable.Range(0, 8000)
            .Select(i => (short)(Math.Sin(i * 0.07) * 20000))
            .ToArray();

        var decoded = SoundCodec.Decode(SoundEncoder.Encode(pcm), pcm.Length);
        double snr = SignalToNoiseDb(pcm, decoded);
        _output.WriteLine($"sine SNR {snr:0.0} dB");
        Assert.True(snr > 30, $"sine round-trip SNR was only {snr:0.0} dB");
    }

    /// <summary>
    /// The real test: take samples out of the ROM, re-encode them, and check the result stays close.
    /// </summary>
    [RomFact]
    public void RomSamplesSurviveAReEncode()
    {
        var bank = SoundDirectory.Read(TestRom.Rom, AssetDirectory.Read(TestRom.Rom));
        var picks = bank.Samples.Where(s => s.DeclaredLength > 8000).Take(12).ToList();
        Assert.NotEmpty(picks);

        double worst = double.MaxValue;
        foreach (var s in picks)
        {
            var original = bank.Decode(s);
            var again = SoundCodec.Decode(SoundEncoder.Encode(original), original.Length);

            double snr = SignalToNoiseDb(original, again);
            _output.WriteLine($"sample {s.Index,5} {s.SampleRate,6} Hz  {original.Length,7:N0} samples  SNR {snr:0.0} dB");
            worst = Math.Min(worst, snr);
        }

        Assert.True(worst > 18, $"worst re-encode SNR was {worst:0.0} dB");
    }

    /// <summary>An encoded blob must declare only predictors and shifts the decoder accepts.</summary>
    [Fact]
    public void HeadersStayInRange()
    {
        var rng = new Random(11);
        var pcm = Enumerable.Range(0, 5000)
            .Select(i => (short)(Math.Sin(i * 0.03) * 12000 + rng.Next(-800, 800)))
            .ToArray();

        var blob = SoundEncoder.Encode(pcm);
        for (int at = SoundCodec.BookBytes; at + SoundCodec.FrameBytes <= blob.Length; at += SoundCodec.FrameBytes)
            foreach (int run in new[] { 0, 1 })
            {
                int header = blob[at + 8 + run * 16];
                Assert.InRange(header >> 4, 0, SoundCodec.PredictorCount - 1);
                Assert.InRange(header & 0xF, 0, SoundCodec.ShiftBias);
            }
    }

    /// <summary>The generated codebook must be readable as order-2 impulse responses, like the ROM's.</summary>
    [Fact]
    public void GeneratedCodebookIsAnImpulseResponseTable()
    {
        var pcm = Enumerable.Range(0, 6000)
            .Select(i => (short)(Math.Sin(i * 0.05) * 18000))
            .ToArray();

        var blob = SoundEncoder.Encode(pcm);
        for (int p = 0; p < SoundCodec.PredictorCount; p++)
        {
            int a2 = BinaryPrimitives.ReadInt16BigEndian(blob.AsSpan(p * 32, 2));
            int a1 = BinaryPrimitives.ReadInt16BigEndian(blob.AsSpan(p * 32 + 16, 2));
            int stored = BinaryPrimitives.ReadInt16BigEndian(blob.AsSpan(p * 32 + 18, 2));
            if (a1 == 0 && a2 == 0) continue;

            int predicted = (a1 * a1 + 2048 * a2) / 2048;
            Assert.InRange(predicted - stored, -8, 8);
        }
    }
}
