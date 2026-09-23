using System;
using System.Linq;
using System.Text;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

public sealed class TextTableTests
{
    private readonly ITestOutputHelper _output;
    public TextTableTests(ITestOutputHelper output) => _output = output;

    [RomFact]
    public void FindsTheGameTextAsOneContiguousRegion()
    {
        var rom = TestRom.Rom;
        var entries = TextTable.Read(rom, AssetDirectory.Read(rom));

        _output.WriteLine($"{entries.Count} entries, ids {entries[0].AssetId}..{entries[^1].AssetId}, " +
                          $"{entries.Sum(e => (long)e.Text.Length):N0} characters");

        Assert.True(entries.Count > 190, $"only {entries.Count} text assets were found");
        Assert.InRange(entries[0].AssetId, 2100, 2200);
        Assert.InRange(entries[^1].AssetId, 2300, 2400);

        // The text lives in one region; nothing should be detected far outside it.
        Assert.All(entries, e => Assert.InRange(e.AssetId, 2000, 2500));
    }

    /// <summary>The point worth pinning: there is no character table and no control codes.</summary>
    [RomFact]
    public void TextIsPlainAsciiWithCrlfAndNoControlCodes()
    {
        var rom = TestRom.Rom;
        foreach (var entry in TextTable.Read(rom, AssetDirectory.Read(rom)))
            foreach (char c in entry.Text)
                Assert.True(c is '\r' or '\n' || (c >= 32 && c < 127),
                            $"asset {entry.AssetId} contains U+{(int)c:X4}");
    }

    /// <summary>Decoding then re-encoding must be lossless, or an untouched string would drift.</summary>
    [RomFact]
    public void EveryStringRoundTripsThroughEncode()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        foreach (var entry in TextTable.Read(rom, directory))
        {
            var bytes = TextTable.Encode(entry.Text);
            Assert.Equal(entry.Text, TextTable.Decode(bytes));
        }
    }

    /// <summary>The decoded bytes must match what the asset actually holds, byte for byte.</summary>
    [RomFact]
    public void EncodedBytesMatchTheStoredAsset()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        int compared = 0;

        foreach (var entry in TextTable.Read(rom, directory))
        {
            var asset = directory.Entries.First(e => e.Index == entry.AssetId);
            Assert.True(directory.TryGetData(rom, asset, out var data));
            Assert.Equal(data, TextTable.Encode(entry.Text));
            compared++;
        }

        Assert.True(compared > 190, $"only {compared} strings were compared");
    }

    [Fact]
    public void LoneNewlinesAreNormalisedToCrlf()
    {
        Assert.Equal("a\r\nb\r\nc", TextTable.Decode(TextTable.Encode("a\nb\r\nc")));
        Assert.Equal("a\r\nb", TextTable.Decode(TextTable.Encode("a\rb")));
    }

    [Fact]
    public void NonAsciiIsRefusedRatherThanMangled()
    {
        var ex = Assert.Throws<ArgumentException>(() => TextTable.Encode("café"));
        Assert.Contains("U+00E9", ex.Message);

        Assert.Throws<ArgumentException>(() => TextTable.Encode("bell"));
    }

    [Fact]
    public void BinaryBlobsAreNotMistakenForText()
    {
        // A texture palette: printable by accident, but no letters or spacing.
        var palette = Enumerable.Range(0, 256).Select(i => (byte)(0x21 + i % 60)).ToArray();
        Assert.False(TextTable.LooksLikeText(palette));

        // Anything with a control byte is out.
        Assert.False(TextTable.LooksLikeText(Encoding.ASCII.GetBytes("Hello there\0world")));

        Assert.True(TextTable.LooksLikeText(Encoding.ASCII.GetBytes(" August 8th\r\n I talked to the chief.")));
    }
}
