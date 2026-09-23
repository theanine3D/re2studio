using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Codecs;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

public sealed class PackBitsTests
{
    private readonly ITestOutputHelper _output;
    public PackBitsTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Every RLE asset in the ROM must decode to exactly the length its own trailer declares.
    /// </summary>
    [RomFact]
    public void EveryRleAssetDecodesToItsDeclaredLength()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        int rle = 0, rle16 = 0;
        foreach (var entry in directory.Entries.Where(e => e.Kind is AssetKind.Rle or AssetKind.Rle16))
        {
            Assert.True(directory.TryGetData(rom, entry, out var data),
                        $"asset {entry.Index} ({entry.Kind}) would not decode");
            Assert.Equal(entry.DecompressedSize, data.Length);
            if (entry.Kind == AssetKind.Rle) rle++; else rle16++;
        }

        _output.WriteLine($"decoded {rle} type-2 and {rle16} type-4 assets");
        Assert.True(rle > 100, $"only {rle} type-2 assets were seen");
        Assert.True(rle16 > 0, "no type-4 assets were seen");
    }

    /// <summary>Re-encoding has to be lossless, or an edited asset would come back corrupt.</summary>
    [RomFact]
    public void EveryRleAssetSurvivesAReEncode()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        long original = 0, again = 0;
        foreach (var entry in directory.Entries.Where(e => e.Kind is AssetKind.Rle or AssetKind.Rle16))
        {
            Assert.True(directory.TryGetData(rom, entry, out var data));

            var encoded = entry.Kind == AssetKind.Rle
                ? PackBits.EncodeBytes(data)
                : PackBits.EncodeUnits(data);
            var decoded = entry.Kind == AssetKind.Rle
                ? PackBits.DecodeBytes(encoded)
                : PackBits.DecodeUnits(encoded);

            Assert.Equal(data, decoded);
            original += entry.StoredSize;
            again += encoded.Length;
        }

        _output.WriteLine($"re-encoded {again:N0} bytes where the game used {original:N0}");
    }

    [Fact]
    public void ByteRleRoundTripsRunsAndLiterals()
    {
        var source = new byte[] { 1, 2, 3 }
            .Concat(Enumerable.Repeat((byte)0xAA, 300))
            .Concat(new byte[] { 9, 8, 7, 6 })
            .Concat(Enumerable.Repeat((byte)0x00, 5))
            .ToArray();

        Assert.Equal(source, PackBits.DecodeBytes(PackBits.EncodeBytes(source)));
    }

    [Fact]
    public void UnitRleRoundTripsRunsAndLiterals()
    {
        var source = new byte[] { 0xBE, 0x1E, 0x00, 0x40 }
            .Concat(Enumerable.Repeat((byte)0xFF, 400))     // 200 units of FFFF
            .Concat(new byte[] { 0x12, 0x34, 0x56, 0x78 })
            .ToArray();

        Assert.Equal(source, PackBits.DecodeUnits(PackBits.EncodeUnits(source)));
    }

    /// <summary>A long run must compress, which is the whole point of the 16-bit variant.</summary>
    [Fact]
    public void UnitRleCollapsesLongHalfwordRuns()
    {
        var source = Enumerable.Repeat((byte)0, 4000).ToArray();
        var encoded = PackBits.EncodeUnits(source);

        Assert.True(encoded.Length < 32, $"a 4,000-byte zero run encoded to {encoded.Length} bytes");
        Assert.Equal(source, PackBits.DecodeUnits(encoded));
    }

    [Fact]
    public void EmptyInputEncodesToJustATerminator()
    {
        Assert.Empty(PackBits.DecodeBytes(PackBits.EncodeBytes(Array.Empty<byte>())));
        Assert.Empty(PackBits.DecodeUnits(PackBits.EncodeUnits(Array.Empty<byte>())));
    }
}
