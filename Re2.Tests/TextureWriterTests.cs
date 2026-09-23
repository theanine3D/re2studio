using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

public sealed class TextureWriterTests
{
    private readonly ITestOutputHelper _output;
    public TextureWriterTests(ITestOutputHelper output) => _output = output;

    private static List<TextureFile> LoadTextures(int take = int.MaxValue)
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var textures = new List<TextureFile>();

        foreach (var entry in directory.Entries)
        {
            if (textures.Count >= take) break;
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (TextureFile.TryParse(data, out var texture)) textures.Add(texture);
        }

        return textures;
    }

    /// <summary>
    /// The property that makes the importer safe to use on the game's own art: decoding a texture and
    /// re-encoding it must be exact.
    /// </summary>
    [RomFact]
    public void EveryPalettedTextureReEncodesExactly()
    {
        var textures = LoadTextures();
        Assert.True(textures.Count > 1000, $"only {textures.Count} textures were found");

        int checkedCount = 0, exact = 0;

        foreach (var texture in textures.Where(t => t.IsPaletted))
        {
            var rgba = texture.ToRgba();
            var (bytes, report) = TextureWriter.Replace(texture, rgba, texture.Width, texture.Height);
            var rebuilt = TextureFile.Parse(bytes);

            Assert.Equal(texture.Width, rebuilt.Width);
            Assert.Equal(texture.Height, rebuilt.Height);
            Assert.Equal(texture.PaletteCount, rebuilt.PaletteCount);
            Assert.Equal(texture.BitsPerPixel, rebuilt.BitsPerPixel);
            Assert.Equal(texture.Format, rebuilt.Format);

            // Pixels must decode to the same colours, whatever palette order was chosen.
            Assert.Equal(rgba, rebuilt.ToRgba());

            checkedCount++;
            if (report.Lossless) exact++;
        }

        _output.WriteLine($"{checkedCount:N0} paletted textures re-encoded; {exact:N0} kept their exact palette");
        Assert.Equal(checkedCount, exact);
    }

    [RomFact]
    public void GreyscaleTexturesReEncodeExactly()
    {
        var grey = LoadTextures().Where(t => !t.IsPaletted).ToList();
        Assert.NotEmpty(grey);

        foreach (var texture in grey)
        {
            var rgba = texture.ToRgba();
            var (bytes, _) = TextureWriter.Replace(texture, rgba, texture.Width, texture.Height);
            Assert.Equal(rgba, TextureFile.Parse(bytes).ToRgba());
        }

        _output.WriteLine($"{grey.Count} greyscale textures re-encoded exactly");
    }

    /// <summary>Replacing at the wrong size has to fail loudly: the UVs and bit depth depend on it.</summary>
    [RomFact]
    public void WrongDimensionsAreRejected()
    {
        var texture = LoadTextures(1)[0];
        var wrong = new byte[(texture.Width + 1) * texture.Height * 4];

        var ex = Assert.Throws<System.IO.InvalidDataException>(
            () => TextureWriter.Replace(texture, wrong, texture.Width + 1, texture.Height));
        Assert.Contains("Dimensions are fixed", ex.Message);
    }

    /// <summary>
    /// A photographic image has far more colours than the palette holds, so it must be quantised
    /// rather than refused -- and the result has to stay recognisably the same picture.
    /// </summary>
    [RomFact]
    public void AnImageWithTooManyColoursIsQuantisedSensibly()
    {
        var texture = LoadTextures().First(t => t.PaletteCount == 256 && t.Width >= 32);

        // A smooth gradient in all three channels: thousands of distinct 5-bit colours.
        var rgba = new byte[texture.Width * texture.Height * 4];
        for (int y = 0; y < texture.Height; y++)
        for (int x = 0; x < texture.Width; x++)
        {
            int o = (y * texture.Width + x) * 4;
            rgba[o] = (byte)(x * 255 / Math.Max(1, texture.Width - 1));
            rgba[o + 1] = (byte)(y * 255 / Math.Max(1, texture.Height - 1));
            rgba[o + 2] = (byte)((x + y) * 255 / Math.Max(1, texture.Width + texture.Height - 2));
            rgba[o + 3] = 255;
        }

        var (bytes, report) = TextureWriter.Replace(texture, rgba, texture.Width, texture.Height);
        _output.WriteLine(report.ToString());

        Assert.False(report.Lossless);
        Assert.True(report.PaletteEntriesUsed <= texture.PaletteCount);

        var rebuilt = TextureFile.Parse(bytes).ToRgba();
        double error = 0;
        for (int i = 0; i < rgba.Length; i += 4)
            for (int c = 0; c < 3; c++)
                error += Math.Abs(rgba[i + c] - rebuilt[i + c]);

        double mean = error / (rgba.Length / 4 * 3);
        _output.WriteLine($"mean channel error {mean:0.0}/255");
        Assert.True(mean < 12, $"quantisation lost too much: mean channel error {mean:0.0}");
    }

    /// <summary>One bit of alpha means a pixel is either solid or invisible.</summary>
    [RomFact]
    public void OneBitAlphaIsPreserved()
    {
        var texture = LoadTextures().First(t => t.PaletteCount == 16);

        var rgba = new byte[texture.Width * texture.Height * 4];
        for (int i = 0; i < texture.Width * texture.Height; i++)
        {
            int o = i * 4;
            rgba[o] = 200; rgba[o + 1] = 40; rgba[o + 2] = 40;
            rgba[o + 3] = (byte)(i % 2 == 0 ? 255 : 0);
        }

        var (bytes, _) = TextureWriter.Replace(texture, rgba, texture.Width, texture.Height);
        var rebuilt = TextureFile.Parse(bytes).ToRgba();

        for (int i = 0; i < texture.Width * texture.Height; i++)
            Assert.Equal(i % 2 == 0 ? 255 : 0, rebuilt[i * 4 + 3]);
    }

    [Fact]
    public void ColourConversionRoundTripsThroughFiveBits()
    {
        foreach (byte v in new byte[] { 0, 8, 33, 128, 200, 255 })
        {
            ushort packed = TextureWriter.ToRgba5551(v, v, v, 255);
            var (r, g, b, a) = TextureFile.Rgba5551ToRgba8(packed);
            Assert.Equal(255, a);
            // 5-bit precision: at most half a step of 8 either way.
            Assert.InRange(Math.Abs(r - v), 0, 5);
            Assert.Equal(r, g);
            Assert.Equal(g, b);
        }

        Assert.Equal(0, TextureWriter.ToRgba5551(255, 255, 255, 0) & 1);
        Assert.Equal(1, TextureWriter.ToRgba5551(255, 255, 255, 255) & 1);
    }
}
