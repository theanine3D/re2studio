using System;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public class MenuPaletteBuilderTests
{
    private static byte[] Pixels(int count, Func<int, (byte R, byte G, byte B, byte A)> colour)
    {
        var rgba = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            var (r, g, b, a) = colour(i);
            rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
        }
        return rgba;
    }

    [Fact]
    public void FewColoursAreKeptExactly()
    {
        var rgba = Pixels(64, i => ((byte)(i % 8 * 32), 0, 255, 255));
        var result = MenuPaletteBuilder.Build(rgba);

        Assert.False(result.Merged);
        Assert.Equal(8, result.DistinctColours);
        Assert.Equal(8, result.PaletteEntries);
        Assert.Equal(1024, result.PaletteRgba.Length);
    }

    [Fact]
    public void TooManyColoursAreMergedUnderTheLimit()
    {
        // 32*32 distinct 5-bit colours.
        var rgba = Pixels(1024, i => ((byte)(i % 32 << 3), (byte)(i / 32 << 3), 128, 255));
        var result = MenuPaletteBuilder.Build(rgba);

        Assert.True(result.Merged);
        Assert.Equal(1024, result.DistinctColours);
        Assert.InRange(result.PaletteEntries, 200, 256);
    }

    [Fact]
    public void TransparencyTakesEntryZero()
    {
        var rgba = Pixels(16, i => i < 4 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)200, (byte)10, (byte)10, (byte)255));
        var result = MenuPaletteBuilder.Build(rgba);

        Assert.True(result.HasTransparency);
        Assert.Equal(0, result.PaletteRgba[3]);
        Assert.Equal(255, result.PaletteRgba[7]);
        Assert.Equal(2, result.PaletteEntries);
    }

    [Fact]
    public void MatchedPictureReproducesExactColours()
    {
        const int W = 16, H = 16;
        var rgba = Pixels(W * H, i => ((byte)(i * 8 % 256), (byte)(i % 16 * 16), 40, 255));
        var raw = new byte[MenuImage.HeaderSize + W * H + 256 * 2];
        raw[3] = 1; raw[7] = W; raw[11] = H; raw[14] = 1; raw[19] = 1;
        Assert.True(MenuImage.TryParse(raw, out var image));

        var built = MenuPaletteBuilder.Build(rgba);
        Assert.False(built.Merged);

        var result = image!.WithPalette(0, built.PaletteRgba).WithPixels(rgba, W, H, 0).ToRgba(0);
        for (int i = 0; i < rgba.Length; i++)
            Assert.Equal(rgba[i] >> 3, result[i] >> 3);
    }
}
