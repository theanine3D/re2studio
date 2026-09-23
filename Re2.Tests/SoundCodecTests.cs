using System;
using System.Buffers.Binary;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Export;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public sealed class SoundCodecTests
{
    private static SoundDirectory Bank => SoundDirectory.Read(TestRom.Rom, AssetDirectory.Read(TestRom.Rom));

    /// <summary>
    /// The structural invariant the whole format rests on: a sample is a 256-byte codebook followed
    /// by whole 40-byte frames. If this ever fails, the framing is wrong, not the audio.
    /// </summary>
    [RomFact]
    public void EverySampleIsACodebookFollowedByWholeFrames()
    {
        foreach (var s in Bank.Samples)
        {
            Assert.True(s.StoredSize > SoundCodec.BookBytes, $"sample {s.Index} is too small to hold a codebook");
            Assert.Equal(0, (s.StoredSize - SoundCodec.BookBytes) % SoundCodec.FrameBytes);
        }
    }

    /// <summary>
    /// Frame granularity plus the bank's 8-byte alignment always leave a little slack past the
    /// declared length -- never a shortfall, which would mean the frame size is wrong.
    /// </summary>
    [RomFact]
    public void CapacityExceedsTheDeclaredLengthByFrameAndAlignmentSlackOnly()
    {
        foreach (var s in Bank.Samples)
        {
            int slack = SoundCodec.CapacityInSamples(s.StoredSize) - (int)s.DeclaredLength;
            Assert.InRange(slack, 0, 4 * SoundCodec.SamplesPerFrame);
        }
    }

    /// <summary>
    /// The codebook rows really are impulse responses of a 2-pole filter, which is what licenses
    /// reading the coefficients out of position 0.
    /// </summary>
    [RomFact]
    public void CodebookRowsAreOrder2ImpulseResponses()
    {
        int checkedRows = 0;
        foreach (var s in Bank.Samples.Take(200))
        {
            var raw = Bank.GetEncoded(s);
            if (raw.Length < SoundCodec.BookBytes) continue;

            for (int p = 0; p < SoundCodec.PredictorCount; p++)
            {
                int a2 = BinaryPrimitives.ReadInt16BigEndian(raw.Slice(p * 32, 2));
                int a1 = BinaryPrimitives.ReadInt16BigEndian(raw.Slice(p * 32 + 16, 2));
                int stored = BinaryPrimitives.ReadInt16BigEndian(raw.Slice(p * 32 + 18, 2));
                if (a1 == 0 && a2 == 0) continue;

                int predicted = (a1 * a1 + 2048 * a2) / 2048;
                Assert.InRange(predicted - stored, -8, 8);
                checkedRows++;
            }
        }
        Assert.True(checkedRows > 500, $"only {checkedRows} predictors were checked");
    }

    /// <summary>Every sample decodes to exactly its declared length.</summary>
    [RomFact]
    public void EverySampleDecodesToItsDeclaredLength()
    {
        var bank = Bank;
        foreach (var s in bank.Samples)
            Assert.Equal((int)s.DeclaredLength, bank.Decode(s).Length);
    }

    /// <summary>A wrong predictor, shift or nibble order sends the filter into the rails.</summary>
    [RomFact]
    public void DecodingTheWholeBankBarelyClips()
    {
        var bank = Bank;
        long total = 0, clipped = 0;

        foreach (var s in bank.Samples)
            foreach (var v in bank.Decode(s))
            {
                total++;
                if (v is short.MaxValue or short.MinValue) clipped++;
            }

        Assert.True(total > 10_000_000, $"only {total:N0} samples decoded");
        Assert.True(clipped < total / 200, $"{100.0 * clipped / total:0.00}% of samples clipped");
    }

    /// <summary>The test that pinned the layout down.</summary>
    [RomFact]
    public void SeedSamplesJoinTheAdpcmRunsWithoutADiscontinuity()
    {
        var bank = Bank;
        var loud = bank.Samples.Where(s => s.DeclaredLength > 8000).Take(40).ToList();
        Assert.NotEmpty(loud);

        foreach (var s in loud)
        {
            var pcm = bank.Decode(s);
            double seam = 0, interior = 0;
            int seamCount = 0, interiorCount = 0;

            for (int i = 1; i < pcm.Length; i++)
            {
                double step = Math.Abs(pcm[i] - pcm[i - 1]);
                // Positions 0..2 of each 32-sample run are the two seed samples and the first
                // predicted one -- exactly where a bad layout shows up.
                if (i % 32 <= 2) { seam += step; seamCount++; }
                else { interior += step; interiorCount++; }
            }

            if (seamCount == 0 || interiorCount == 0) continue;
            double ratio = (seam / seamCount) / Math.Max(1.0, interior / interiorCount);
            Assert.InRange(ratio, 0.5, 1.6);
        }
    }

    [RomFact]
    public void WavExportCarriesTheSampleRateAndPcm()
    {
        var bank = Bank;
        var sample = bank.Samples.First(s => s.DeclaredLength > 4000);
        var pcm = bank.Decode(sample);

        var (readBack, rate) = WavCodec.Read(bank.ToWav(sample));

        Assert.Equal(sample.SampleRate, rate);
        Assert.Equal(pcm, readBack);
    }

    [Fact]
    public void WavRoundTripsArbitraryPcm()
    {
        var pcm = Enumerable.Range(0, 1000)
            .Select(i => (short)(Math.Sin(i * 0.05) * 30000))
            .ToArray();

        var (readBack, rate) = WavCodec.Read(WavCodec.Write(pcm, 22050));

        Assert.Equal(22050, rate);
        Assert.Equal(pcm, readBack);
    }
}
