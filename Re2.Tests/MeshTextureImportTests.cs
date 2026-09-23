using System.Collections.Generic;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Core.Import;
using Xunit;

namespace Re2.Tests;

/// <summary>Importing a GLB brings its textures with it.</summary>
public class MeshTextureImportTests
{
    /// <summary>A solid-colour PNG of the given size, as an exportable texture.</summary>
    private static ExportTexture SolidTexture(int width, int height, byte r, byte g, byte b)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
        }

        return new ExportTexture(ImageCodec.RgbaToPng(rgba, width, height), width, height);
    }

    /// <summary>
    /// A glTF written with textures must give those same images back, under the slot numbers their
    /// materials were named for.
    /// </summary>
    [RomFact]
    public void TexturesSurviveAGltfRoundTrip()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        var entry = directory.Entries.First(e =>
            directory.TryGetData(rom, e, out var data) && MeshFile.TryParse(data, out _));
        Assert.True(directory.TryGetData(rom, entry, out var meshData));
        var mesh = MeshFile.Parse(meshData);

        var textures = new List<ExportTexture>
        {
            SolidTexture(16, 16, 200, 30, 30),
            SolidTexture(8, 8, 30, 200, 30),
        };

        string path = Path.Combine(Path.GetTempPath(), "re2-tex-" + Path.GetRandomFileName()[..6] + ".glb");
        try
        {
            GltfExporter.Save(mesh, textures, path, "textured");

            var read = GltfImporter.LoadTextureImages(path, textures.Count);

            // Only slots the model actually uses are written, so check what came back is a subset of
            // what went in, and that at least one made the trip.
            Assert.NotEmpty(read);

            foreach (var (slot, png) in read)
            {
                Assert.InRange(slot, 0, textures.Count - 1);

                // The image is the one that belongs to that slot, at its own size.
                using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(png);
                Assert.Equal(textures[slot].Width, image.Width);
                Assert.Equal(textures[slot].Height, image.Height);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A material with no <c>texNN</c> name binds no slot, so its image must not be attributed to one
    /// -- writing it over an arbitrary texture would be worse than ignoring it.
    /// </summary>
    [RomFact]
    public void MaterialsWithoutASlotNameAreIgnored()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        var entry = directory.Entries.First(e =>
            directory.TryGetData(rom, e, out var data) && MeshFile.TryParse(data, out _));
        Assert.True(directory.TryGetData(rom, entry, out var meshData));
        var mesh = MeshFile.Parse(meshData);

        string path = Path.Combine(Path.GetTempPath(), "re2-notex-" + Path.GetRandomFileName()[..6] + ".glb");
        try
        {
            // Exported with no textures at all: every primitive gets the "untextured" material.
            GltfExporter.Save(mesh, path, "plain");

            Assert.Empty(GltfImporter.LoadTextureImages(path, 8));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
