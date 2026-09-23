using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Codecs;
using Xunit;

namespace Re2.Tests;

public class BitStreamCodecTests
{
    /// <summary>Assets reached through the per-character table at RAM 0x80126C80, entries [1],[3],[5].</summary>
    private static readonly int[] AnimationAssets = { 5593, 5595, 5597, 5601, 5604 };

    private static BitStreamResult Decode(int assetId)
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);
        var entry = dir.Entries.Single(e => e.Index == assetId);
        Assert.True(dir.TryGetData(rom, entry, out var data));
        Assert.True(BitStreamCodec.TryDecode(data, out var result), $"asset {assetId} did not decode");
        return result;
    }

    /// <summary>The decisive check.</summary>
    [RomFact]
    public void TableChainsExactlyForEveryAnimationAsset()
    {
        foreach (int id in AnimationAssets)
        {
            var result = Decode(id);
            Assert.True(BitStreamCodec.HeaderChains(result), $"asset {id} table does not chain");
        }
    }

    [RomFact]
    public void DecodesTheKnownClipTable()
    {
        var table = Decode(5593).Table.TakeWhile(t => t.Count > 0).ToList();

        Assert.Equal(8, table.Count);
        Assert.Equal((0x20, 65), table[0]);
        Assert.Equal((0x124, 44), table[1]);
        Assert.Equal((0x5F4, 38), table[^1]);

        // The first record starts right after the header slots, so offsets are relative to the
        // decoded buffer rather than to the payload.
        Assert.Equal(table.Count * 4, table[0].Offset);
    }

    [RomFact]
    public void ExpandsWellBeyondTheSourceSize()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);

        foreach (int id in AnimationAssets)
        {
            var entry = dir.Entries.Single(e => e.Index == id);
            dir.TryGetData(rom, entry, out var data);
            var result = Decode(id);

            Assert.True(result.Data.Length * 4 > data.Length * 4,
                $"asset {id} decoded to {result.Data.Length * 4} bytes from {data.Length}");
        }
    }

    [Fact]
    public void RejectsTooShortInput() => Assert.False(BitStreamCodec.TryDecode(new byte[4], out _));
}
