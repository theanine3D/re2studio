using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public class TextureFileTests
{
    private static List<(AssetEntry Entry, TextureFile Texture)> LoadTextures()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);
        var result = new List<(AssetEntry, TextureFile)>();

        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (TextureFile.TryParse(data, out var texture)) result.Add((entry, texture));
        }
        return result;
    }

    [RomFact]
    public void FindsTheTextureSet()
    {
        // 1,054 rather than 1,053: one texture is stored with the type-2 RLE codec, so it only
        // became readable once that codec was decoded.
        var textures = LoadTextures();
        Assert.Equal(1054, textures.Count);
        Assert.Equal(5922, textures[0].Entry.Index);
        Assert.Equal(6975, textures[^1].Entry.Index);
    }

    /// <summary>
    /// The decisive check: width times the derived height must consume the pixel data exactly, for
    /// every texture. A wrong width or bit depth breaks this immediately.
    /// </summary>
    [RomFact]
    public void DimensionsAccountForEveryPixelByte()
    {
        foreach (var (entry, texture) in LoadTextures())
        {
            int pixelBytes = texture.PackedPixels.Length;
            int expected = texture.BitsPerPixel == 4
                ? (texture.Width * texture.Height + 1) / 2
                : texture.Width * texture.Height;

            Assert.True(pixelBytes == expected,
                $"asset {entry.Index}: {texture.Width}x{texture.Height} @{texture.BitsPerPixel}bpp needs {expected} bytes, has {pixelBytes}");
        }
    }

    [RomFact]
    public void BitDepthFollowsPaletteSize()
    {
        foreach (var (_, texture) in LoadTextures())
        {
            Assert.Contains(texture.PaletteCount, new[] { 0, 16, 256 });
            Assert.Equal(texture.PaletteCount == 16 ? 4 : 8, texture.BitsPerPixel);
        }
    }

    /// <summary>Every palette index a texture uses must exist in its palette.</summary>
    [RomFact]
    public void PixelIndicesStayInsideThePalette()
    {
        foreach (var (entry, texture) in LoadTextures())
        {
            if (!texture.IsPaletted) continue;
            for (int y = 0; y < texture.Height; y++)
                for (int x = 0; x < texture.Width; x++)
                    Assert.True(texture.PixelIndex(x, y) < texture.PaletteCount,
                        $"asset {entry.Index} at ({x},{y}) indexes past a {texture.PaletteCount}-entry palette");
        }
    }

    [RomFact]
    public void DecodesEveryTextureToFullRgba()
    {
        foreach (var (_, texture) in LoadTextures())
            Assert.Equal(texture.Width * texture.Height * 4, texture.ToRgba().Length);
    }

    [RomFact]
    public void TexturesAreNotBlank()
    {
        // A decode that silently produced one flat colour would still pass the size checks.
        foreach (var (entry, texture) in LoadTextures().Take(200))
        {
            var rgba = texture.ToRgba();
            var distinct = new HashSet<uint>();
            for (int i = 0; i < rgba.Length; i += 4)
                distinct.Add((uint)(rgba[i] << 24 | rgba[i + 1] << 16 | rgba[i + 2] << 8 | rgba[i + 3]));
            Assert.True(distinct.Count > 1, $"asset {entry.Index} decoded to a single flat colour");
        }
    }

    [Theory]
    [InlineData(0xFFFF, 255, 255, 255, 255)]
    [InlineData(0x0001, 0, 0, 0, 255)]
    [InlineData(0x0000, 0, 0, 0, 0)]
    public void ExpandsRgba5551(int value, int r, int g, int b, int a)
    {
        var (rr, gg, bb, aa) = TextureFile.Rgba5551ToRgba8((ushort)value);
        Assert.Equal((r, g, b, a), (rr, gg, bb, (int)aa));
    }

    [Fact]
    public void RejectsNonTextureData() => Assert.False(TextureFile.TryParse(new byte[64], out _));
}
