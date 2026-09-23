using System;
using System.Linq;
using Re2.Core.Emulation;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>
/// Writing a MORT bitstream, checked with the one payload whose correct output is known exactly.
/// </summary>
public class MortSilenceTests
{
    private readonly ITestOutputHelper _out;

    public MortSilenceTests(ITestOutputHelper output) => _out = output;

    [RomFact]
    public void SilenceWeWriteDecodesToExactZeros()
    {
        var cpu = GameCode.Load(TestRom.Rom);

        foreach (int blocks in new[] { 1, 15, 16, 17, 100, 313 })
        {
            var clip = MortCodec.Silence(blocks, 16000);
            var samples = MortCodec.Decode(cpu, clip, blocks);

            Assert.Equal(blocks * MortCodec.SamplesPerBlock, samples.Length);
            Assert.All(samples, s => Assert.Equal(0, s));

            _out.WriteLine($"{blocks,4} blocks -> {clip.Length,6} bytes, all {samples.Length,6} samples zero");
        }
    }

    /// <summary>
    /// Silence is compact, which is what makes it usable as padding: a run covers sixteen blocks in
    /// five bits, so padding costs a fraction of a byte per block.
    /// </summary>
    [RomFact]
    public void SilenceCostsAlmostNothingToStore()
    {
        var clip = MortCodec.Silence(1000, 16000);

        _out.WriteLine($"1000 blocks (10 s) of silence: {clip.Length} bytes");
        Assert.True(clip.Length < 100, $"silence took {clip.Length} bytes, which is more than expected");
    }

    [RomFact]
    public void TheHeaderWeWriteIsReadBackTheSameWay()
    {
        var clip = MortCodec.Silence(313, 8000);

        // The rate lives in two places: the clip's own header, and the flag byte of the bank entry that
        // points at it.
        var read = Re2.Core.Assets.VoiceBank.Read(BankOf(clip));

        Assert.Single(read);
        Assert.Equal(313, read[0].BlockCount);
        Assert.Equal(8000, read[0].SampleRate);

        Assert.Equal(8000, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(clip.AsSpan(6)));
    }

    /// <summary>Wraps a single clip in a one-entry bank.</summary>
    private static byte[] BankOf(byte[] clip)
    {
        int rate = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(clip.AsSpan(6));
        uint flag = rate == Re2.Core.Assets.VoiceBank.LowRate ? 0u : 1u;

        var bank = new byte[8 + clip.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bank, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(4), (flag << 24) | 8);
        clip.CopyTo(bank, 8);
        return bank;
    }
}
