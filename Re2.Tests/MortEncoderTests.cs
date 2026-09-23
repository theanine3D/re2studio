using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>The MORT encoder, measured by re-encoding the game's own speech.</summary>
public sealed class MortEncoderTests
{
    private readonly ITestOutputHelper _out;
    public MortEncoderTests(ITestOutputHelper o) => _out = o;

    private static short[] ClipAudio(out int sampleRate, out int blocks, int index = 231)
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == VoiceBank.AssetId);
        directory.TryGetData(rom, entry, out var bank);

        var clip = VoiceBank.Read(bank)[index];
        sampleRate = clip.SampleRate;
        blocks = clip.BlockCount;

        return MortStream.Decode(bank.AsSpan(clip.Offset, clip.StoredSize), clip.BlockCount);
    }

    /// <summary>The companding inverse has to undo the decoder's exactly, or nothing else lines up.</summary>
    [Fact]
    public void ExpandUndoesCompand()
    {
        for (int value = -32768; value <= 32767; value += 7)
        {
            int companded = MortDecoder.Compand(value);
            if (Math.Abs(companded) >= 0x7FFF) continue;          // saturated: not invertible, by design

            int back = MortEncoder.Expand(companded);
            Assert.True(Math.Abs(back - value) <= 4, $"{value} -> {companded} -> {back}");
        }
    }

    /// <summary>
    /// Re-encoding the game's own speech and decoding it again must land close to where it started.
    /// </summary>
    [RomFact]
    public void ReEncodingTheGamesSpeechStaysCloseToIt()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == VoiceBank.AssetId);
        directory.TryGetData(rom, entry, out var bank);
        var clips = VoiceBank.Read(bank);

        double worst = double.MaxValue, worstCorrelation = 1;

        foreach (int index in new[] { 17, 100, 231, 300, 417, 500 })
        {
            var clip = clips[index];
            var original = MortStream.Decode(bank.AsSpan(clip.Offset, clip.StoredSize), clip.BlockCount);

            var encoded = MortEncoder.Encode(original, clip.SampleRate, out var report);
            var again = MortStream.Decode(encoded, MortCodec.ReadBlockCount(encoded));

            double correlation = Correlation(original, again);
            _out.WriteLine($"clip {index}: {clip.BlockCount} blocks, " +
                           $"{clip.StoredSize:N0} bytes original vs {encoded.Length:N0} re-encoded, " +
                           $"SNR {report.SignalToNoise:0.0} dB, correlation {correlation:0.000}");

            Assert.Equal(original.Length, again.Length);
            worst = Math.Min(worst, report.SignalToNoise);
            worstCorrelation = Math.Min(worstCorrelation, correlation);
        }

        _out.WriteLine($"worst SNR {worst:0.0} dB, worst correlation {worstCorrelation:0.000}");

        Assert.True(worst > 2.0, $"worst clip only reached {worst:0.0} dB");
        Assert.True(worstCorrelation > 0.60, $"worst correlation only {worstCorrelation:0.000}");
    }

    /// <summary>
    /// A slot cannot grow, so a replacement dense enough to overshoot has to be made to fit rather
    /// than refused. Loud noise is the worst case: nothing in it is quiet enough to drop for free.
    /// </summary>
    [RomFact]
    public void EncodingToFitNeverOverrunsTheSlot()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == VoiceBank.AssetId);
        directory.TryGetData(rom, entry, out var bank);
        var clips = VoiceBank.Read(bank);

        var random = new Random(7);

        foreach (int index in new[] { 100, 231, 417 })
        {
            var clip = clips[index];

            var noise = new short[clip.BlockCount * 160];
            for (int i = 0; i < noise.Length; i++) noise[i] = (short)random.Next(-20000, 20000);

            var encoded = MortEncoder.EncodeToFit(noise, clip.SampleRate, clip.StoredSize,
                                                  clip.BlockCount, out var report, out string note);

            _out.WriteLine($"clip {index}: slot {clip.StoredSize:N0} bytes, encoded {encoded.Length:N0}, " +
                           $"{report.SilentBlocks:N0}/{report.Blocks:N0} silent. {note}");

            Assert.True(encoded.Length <= clip.StoredSize,
                        $"clip {index} overran its slot: {encoded.Length:N0} > {clip.StoredSize:N0}");
            Assert.Equal(clip.BlockCount, MortCodec.ReadBlockCount(encoded));
        }
    }

    private static double Correlation(short[] a, short[] b)
    {
        double sa = 0, sb = 0, sab = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            sa += (double)a[i] * a[i];
            sb += (double)b[i] * b[i];
            sab += (double)a[i] * b[i];
        }
        return sab / Math.Max(1e-9, Math.Sqrt(sa * sb));
    }
}
