using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Emulation;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>The managed MORT decoder against the cart's own, clip by clip.</summary>
public sealed class MortDecoderTests
{
    private readonly ITestOutputHelper _out;
    public MortDecoderTests(ITestOutputHelper o) => _out = o;

    [RomFact]
    public void ItMatchesTheCartsOwnDecoderSampleForSample()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == VoiceBank.AssetId);
        Assert.True(directory.TryGetData(rom, entry, out var bank));

        var clips = VoiceBank.Read(bank);
        var cpu = GameCode.Load(rom);

        // A spread across the bank: the first few, some in the middle, the last.
        var picks = new[] { 0, 1, 2, 17, 100, 231, 300, 417, clips.Count - 1 };
        int checkedSamples = 0;

        foreach (int index in picks)
        {
            var clip = clips[index];
            var data = bank.AsSpan(clip.Offset, clip.StoredSize).ToArray();
            int blocks = clip.BlockCount;
            if (blocks <= 0) continue;

            var expected = MortCodec.Decode(cpu, data, blocks);
            var actual = MortStream.Decode(data, blocks);

            Assert.Equal(expected.Length, actual.Length);

            int firstDifference = -1;
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i]) { firstDifference = i; break; }

            _out.WriteLine($"clip {index}: {blocks} blocks, {expected.Length:N0} samples -- " +
                           (firstDifference < 0 ? "identical"
                                                : $"differs at {firstDifference} " +
                                                  $"({expected[firstDifference]} vs {actual[firstDifference]})"));

            Assert.Equal(-1, firstDifference);
            checkedSamples += expected.Length;
        }

        _out.WriteLine($"{checkedSamples:N0} samples matched");
        Assert.True(checkedSamples > 100_000, "not enough audio was compared to mean anything");
    }
}
