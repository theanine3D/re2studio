using System.Collections.Generic;
using System.IO;
using System.Linq;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Studio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Re2.Tests;

/// <summary>
/// The workflow this exists for: export a character, repaint a texture, import the file back, and
/// have both the geometry and the repainted texture land in the project -- rather than the textures
/// having to be imported one at a time afterwards.
/// </summary>
public sealed class MeshImportCarriesTexturesTests : IClassFixture<ProjectFixture>
{
    private readonly ProjectFixture _project;

    public MeshImportCarriesTexturesTests(ProjectFixture project) => _project = project;

    private string ProjectFileFor(int assetId)
    {
        var blob = _project.Manifest!.Blobs.First(b => b.Ids.Contains(assetId));
        return Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
    }

    [RomFact]
    public void ImportingAModelAlsoWritesItsTextures()
    {
        using var session = new RomSession(TestRom.Path!);

        // A character whose textures actually decode, so there is something to repaint.
        var character = session.Characters.FirstOrDefault(c =>
            session.LoadMesh(c.MeshAssetId) is not null &&
            c.Textures.TextureIds.Count > 0 &&
            session.TryGetAsset(c.Textures.TextureIds[0], out var d) && TextureFile.TryParse(d, out _));

        Assert.NotNull(character);

        int textureAssetId = character!.Textures.TextureIds[0];
        Assert.True(session.TryGetAsset(textureAssetId, out var originalTexture));
        Assert.True(TextureFile.TryParse(originalTexture, out var texture));

        var mesh = session.LoadMesh(character.MeshAssetId)!;

        // Repaint slot 0 a flat colour the original certainly is not, at its own size so it fits.
        var repainted = new byte[texture.Width * texture.Height * 4];
        for (int i = 0; i < repainted.Length; i += 4)
        {
            repainted[i] = 255; repainted[i + 1] = 0; repainted[i + 2] = 255; repainted[i + 3] = 255;
        }

        var images = new List<ExportTexture>();
        for (int slot = 0; slot < character.Textures.TextureIds.Count; slot++)
        {
            if (slot == 0)
            {
                images.Add(new ExportTexture(
                    Re2.Core.Assets.ImageCodec.RgbaToPng(repainted, texture.Width, texture.Height),
                    texture.Width, texture.Height));
                continue;
            }

            // Everything else keeps whatever the ROM has, so only slot 0 should change.
            int id = character.Textures.TextureIds[slot];
            if (session.TryGetAsset(id, out var data) && TextureFile.TryParse(data, out var other))
                images.Add(new ExportTexture(
                    Re2.Core.Assets.ImageCodec.RgbaToPng(other.ToRgba(), other.Width, other.Height),
                    other.Width, other.Height));
            else
                images.Add(new ExportTexture(System.Array.Empty<byte>(), 1, 1));
        }

        string glb = Path.Combine(Path.GetTempPath(), "re2-carry-" + Path.GetRandomFileName()[..6] + ".glb");
        string meshFile = ProjectFileFor(character.MeshAssetId);
        string textureFile = ProjectFileFor(textureAssetId);
        byte[] meshBefore = File.ReadAllBytes(meshFile);
        byte[] textureBefore = File.ReadAllBytes(textureFile);

        try
        {
            GltfExporter.Save(mesh, images, glb, "carry");

            string report = AssetIo.ImportMesh(session, _project.Folder, character, glb);

            // It says what it did with them, and it did do something.
            Assert.Contains("textures:", report);
            Assert.Contains("replaced", report);

            // The repainted texture reached the project.
            var after = File.ReadAllBytes(textureFile);
            Assert.NotEqual(textureBefore, after);

            // And it is still a texture of the right shape, not just different bytes.
            Assert.True(TextureFile.TryParse(after, out var written));
            Assert.Equal(texture.Width, written.Width);
            Assert.Equal(texture.Height, written.Height);

            // The quantiser has to land somewhere near the colour that was asked for.
            var pixels = written.ToRgba();
            Assert.True(pixels[0] > 200 && pixels[1] < 80 && pixels[2] > 200,
                        $"expected magenta, got ({pixels[0]}, {pixels[1]}, {pixels[2]})");
        }
        finally
        {
            File.WriteAllBytes(meshFile, meshBefore);
            File.WriteAllBytes(textureFile, textureBefore);
            if (File.Exists(glb)) File.Delete(glb);
        }
    }
}
