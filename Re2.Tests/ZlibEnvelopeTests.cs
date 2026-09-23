using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Codecs;

namespace Re2.Tests;

/// <summary>The compressed-asset envelope, pinned against the cart.</summary>
public class ZlibEnvelopeTests
{
    [Fact]
    public void TheHeaderIsAValidZlibHeaderDeclaringA16KbWindow()
    {
        int cmf = Zlib.Header[0], flg = Zlib.Header[1];

        Assert.Equal(8, cmf & 0x0F);                       // CM: deflate
        Assert.Equal(6, cmf >> 4);                         // CINFO: 2^(6+8) = 16 KB
        Assert.Equal(Zlib.WindowSize, 1 << ((cmf >> 4) + 8));
        Assert.Equal(0, (flg >> 5) & 1);                   // FDICT: no preset dictionary
        Assert.Equal(0, (cmf * 256 + flg) % 31);           // the check bits zlib verifies
    }

    [Fact]
    public void Adler32MatchesTheKnownAnswer()
    {
        // The RFC 1950 worked example.
        Assert.Equal(0x11E60398u, Zlib.Adler32("Wikipedia"u8));
        Assert.Equal(1u, Zlib.Adler32(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(64 * 1024)]     // larger than the declared window, so the window check is exercised
    public void WhatWeCompressIsACompleteZlibStream(int length)
    {
        var data = new byte[length];
        new Random(9).NextBytes(data);

        var stored = Zlib.Compress(data);

        Assert.True(Zlib.TryDecompress(stored, out var back), "the stream did not verify");
        Assert.Equal(data, back);

        Assert.Equal(Zlib.Header, stored.Take(Zlib.HeaderSize));
        Assert.True(Zlib.FitsWindow(stored.AsSpan(Zlib.HeaderSize)),
                    "the stream reaches back further than its header allows");
    }

    [Fact]
    public void IncompressibleDataStillFitsTheDeclaredWindow()
    {
        // Random data defeats the compressor, which is when .NET is most likely to reach far back.
        var data = new byte[300 * 1024];
        new Random(4).NextBytes(data);

        var stored = Zlib.Compress(data);

        Assert.True(Zlib.TryDecompress(stored, out var back));
        Assert.Equal(data, back);
        Assert.True(Zlib.FitsWindow(stored.AsSpan(Zlib.HeaderSize)));
    }

    [Fact]
    public void ATruncatedChecksumIsRejected()
    {
        var stored = Zlib.Compress("the quick brown fox"u8);

        Assert.False(Zlib.TryDecompress(stored.AsSpan(0, stored.Length - 1), out _));

        var corrupted = stored.ToArray();
        corrupted[^1] ^= 0xFF;
        Assert.False(Zlib.TryDecompress(corrupted, out _), "a wrong checksum was accepted");
    }

    /// <summary>
    /// Every compressed asset the cart ships is a complete zlib stream whose checksum verifies.
    /// </summary>
    [RomFact]
    public void EveryCompressedRetailAssetVerifiesAsAZlibStream()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        int checked_ = 0, oversizedWindow = 0;

        foreach (var entry in directory.Entries.Where(e => e.Kind == AssetKind.Deflate))
        {
            var stored = rom.Slice(entry.RomOffset, entry.StoredSize);

            Assert.True(Zlib.TryDecompress(stored, out var decoded),
                        $"asset {entry.Index} is not a valid zlib stream");
            Assert.Equal(entry.DecompressedSize, decoded.Length);

            Assert.Equal(Zlib.Header[0], stored[0]);
            Assert.Equal(Zlib.Header[1], stored[1]);

            if (!Zlib.FitsWindow(stored[Zlib.HeaderSize..])) oversizedWindow++;
            checked_++;
        }

        Assert.True(checked_ > 4000, $"only {checked_} compressed assets were checked");
        Assert.Equal(0, oversizedWindow);
    }
}
