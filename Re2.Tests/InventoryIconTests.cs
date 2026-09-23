using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public sealed class InventoryIconTests
{
    private static byte[] Asset(int id)
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        Assert.True(directory.TryGetCartData(rom, directory.Entries.First(e => e.Index == id), out var data));
        return data;
    }

    private static byte[] Palette()
    {
        Assert.True(MenuImage.TryParse(Asset(InventoryIcons.PaletteAsset), out var screen));
        return screen!.PaletteToRgba(InventoryIcons.PaletteIndex);
    }

    [RomFact]
    public void EveryIconAssetIsTheRightSize()
    {
        for (int i = 0; i < InventoryIcons.Count; i++)
            Assert.Equal(InventoryIcons.Size, Asset(InventoryIcons.FirstAsset + i).Length);

        Assert.Equal(InventoryIcons.Size * InventoryIcons.BundleCount,
                     Asset(InventoryIcons.BundleAsset).Length);
    }

    /// <summary>The run is exactly this long: the assets either side are something else.</summary>
    [RomFact]
    public void TheRunEndsWhereItShould()
    {
        Assert.NotEqual(InventoryIcons.Size, Asset(InventoryIcons.FirstAsset - 1).Length);
        Assert.NotEqual(InventoryIcons.Size, Asset(InventoryIcons.FirstAsset + InventoryIcons.Count).Length);
    }

    [RomFact]
    public void ThePaletteScreenHasThePaletteTheIconsUse()
    {
        Assert.True(MenuImage.TryParse(Asset(InventoryIcons.PaletteAsset), out var screen));
        Assert.True(screen!.Palettes.Count > InventoryIcons.PaletteIndex);
    }

    /// <summary>
    /// Pins the palette choice to something measurable rather than to a screenshot: the corner of
    /// every icon is the navy panel it is drawn on, and under the right palette that is a dark blue.
    /// </summary>
    [RomFact]
    public void IconsSitOnANavyPanel()
    {
        var palette = Palette();

        foreach (int item in new[] { 1, 2, 35, 38 })
        {
            var icon = InventoryIcons.All[item];
            var rgba = InventoryIcons.ToRgba(Asset(icon.AssetId), icon, palette);

            int r = rgba[0], g = rgba[1], b = rgba[2];
            Assert.True(b > r + 20 && b > g + 20, $"icon {item} corner is {r},{g},{b}, not navy");
        }
    }

    /// <summary>
    /// Item 0 is "no item", and its icon is the bare panel -- which is a gentle gradient rather than
    /// one flat index, so the check is that every pixel is panel navy.
    /// </summary>
    [RomFact]
    public void TheNoItemIconIsBlank()
    {
        var icon = InventoryIcons.All[0];
        var rgba = InventoryIcons.ToRgba(Asset(icon.AssetId), icon, Palette());

        for (int i = 0; i < InventoryIcons.Size; i++)
        {
            int r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
            Assert.True(b > r && b > g, $"pixel {i} is {r},{g},{b}");
        }
    }

    /// <summary>
    /// An icon exported and imported unchanged must come back byte for byte, or every such round
    /// trip would show up as an edit. The palette has duplicate colours, which is the trap.
    /// </summary>
    [RomFact]
    public void AnUnchangedIconMatchesBackToItsOwnBytes()
    {
        var palette = Palette();

        foreach (var icon in InventoryIcons.All)
        {
            var asset = Asset(icon.AssetId);
            var original = asset.AsSpan(icon.Offset, InventoryIcons.Size).ToArray();
            var rgba = InventoryIcons.ToRgba(asset, icon, palette);

            Assert.Equal(original, InventoryIcons.Match(rgba, original, palette));
        }
    }

    [RomFact]
    public void AnEditedPixelTakesTheNearestColour()
    {
        var palette = Palette();
        var icon = InventoryIcons.All[1];
        var asset = Asset(icon.AssetId);
        var original = asset.AsSpan(0, InventoryIcons.Size).ToArray();

        var rgba = InventoryIcons.ToRgba(asset, icon, palette);
        rgba[0] = 255; rgba[1] = 255; rgba[2] = 255;          // paint the corner white

        var matched = InventoryIcons.Match(rgba, original, palette);
        int c = matched[0] * 4;

        Assert.True(palette[c] > 200 && palette[c + 1] > 200 && palette[c + 2] > 200);
        Assert.Equal(original.Skip(1), matched.Skip(1));
    }

    [Fact]
    public void ReplacingABundledIconTouchesOnlyItsSlice()
    {
        var bundle = new byte[InventoryIcons.Size * InventoryIcons.BundleCount];
        var icon = InventoryIcons.All[InventoryIcons.Count + 3];
        var pixels = Enumerable.Repeat((byte)7, InventoryIcons.Size).ToArray();

        var result = InventoryIcons.Replace(bundle, icon, pixels);

        Assert.All(result.Take(icon.Offset), b => Assert.Equal(0, b));
        Assert.All(result.Skip(icon.Offset).Take(InventoryIcons.Size), b => Assert.Equal(7, b));
        Assert.All(result.Skip(icon.Offset + InventoryIcons.Size), b => Assert.Equal(0, b));
    }

    [Fact]
    public void IconsAreNumberedByItemThenExtras()
    {
        Assert.Equal(InventoryIcons.Count + InventoryIcons.BundleCount, InventoryIcons.All.Count);
        Assert.Equal(38, InventoryIcons.All[38].ItemId);
        Assert.Equal(InventoryIcons.FirstAsset + 38, InventoryIcons.All[38].AssetId);
        Assert.True(InventoryIcons.All[InventoryIcons.Count].InBundle);
        Assert.Equal(-1, InventoryIcons.All[InventoryIcons.Count].ItemId);
    }
}
