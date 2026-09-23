using System;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Project;
using Re2.Core.Rom;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>An icon from PNG to project to built ROM, the way the Icons tab does it.</summary>
public sealed class IconImportTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "re2-icons-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp */ }
    }

    [RomFact]
    public void AnImportedIconReachesTheBuiltRom()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);
        using var session = new RomSession(TestRom.Path!);

        var icon = InventoryIcons.All[38];                     // Green Herb
        string png = AssetIo.ExportIconPng(session, icon, Path.Combine(_folder, "out"));

        // Paint a white stripe across the top row, in a colour the palette really has.
        using (var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(png))
        {
            for (int x = 0; x < InventoryIcons.Width; x++)
                image[x, 0] = new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 255, 255, 255);
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, png);
        }

        string report = AssetIo.ImportIcon(session, _folder, icon, png);
        Assert.Contains("wrote", report);

        var output = RomFile.FromBytes((byte[])TestRom.Rom.Data.Clone());
        var result = ProjectFolder.Build(TestRom.Rom, _folder, output);
        Assert.Equal(1, result.BlobsRebuilt);

        var directory = AssetDirectory.Read(output);
        Assert.True(directory.TryGetCartData(output, directory.Entries.First(e => e.Index == icon.AssetId), out var built));
        Assert.True(MenuImage.TryParse(ReadCart(output, InventoryIcons.PaletteAsset), out var screen));

        var rgba = InventoryIcons.ToRgba(built, icon, screen!.PaletteToRgba(InventoryIcons.PaletteIndex));
        for (int x = 0; x < InventoryIcons.Width; x++)
            Assert.True(rgba[x * 4] > 200 && rgba[x * 4 + 1] > 200 && rgba[x * 4 + 2] > 200, $"pixel {x}");

        // The rest of the icon is untouched, and so is its neighbour.
        Assert.Equal(ReadCart(TestRom.Rom, icon.AssetId).Skip(InventoryIcons.Width),
                     built.Skip(InventoryIcons.Width));
        Assert.Equal(ReadCart(TestRom.Rom, icon.AssetId + 1), ReadCart(output, icon.AssetId + 1));
    }

    /// <summary>Exporting and importing straight back must not count as an edit.</summary>
    [RomFact]
    public void ReimportingAnExportedIconWritesNothing()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);
        using var session = new RomSession(TestRom.Path!);

        foreach (var icon in new[] { InventoryIcons.All[1], InventoryIcons.All[InventoryIcons.Count + 2] })
        {
            string png = AssetIo.ExportIconPng(session, icon, Path.Combine(_folder, "out"));
            Assert.Contains("nothing written", AssetIo.ImportIcon(session, _folder, icon, png));
        }
    }

    [RomFact]
    public void TheWrongSizeIsRefused()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);
        using var session = new RomSession(TestRom.Path!);

        string png = Path.Combine(_folder, "wrong.png");
        File.WriteAllBytes(png, ImageCodec.RgbaToPng(new byte[32 * 32 * 4], 32, 32));

        var error = Assert.Throws<InvalidDataException>(
            () => AssetIo.ImportIcon(session, _folder, InventoryIcons.All[1], png));
        Assert.Contains("40x30", error.Message);
    }

    private static byte[] ReadCart(RomFile rom, int id)
    {
        var directory = AssetDirectory.Read(rom);
        Assert.True(directory.TryGetCartData(rom, directory.Entries.First(e => e.Index == id), out var data));
        return data;
    }
}
