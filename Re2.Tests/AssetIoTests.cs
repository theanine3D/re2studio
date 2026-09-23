using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Project;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>The export and import the window's buttons call.</summary>
public sealed class AssetIoTests : IClassFixture<ProjectFixture>
{
    private readonly ProjectFixture _project;

    public AssetIoTests(ProjectFixture project) => _project = project;

    [RomFact]
    public void ExportsATextureAsPng()
    {
        using var session = new RomSession(TestRom.Path!);
        string folder = Path.Combine(Path.GetTempPath(), "re2-tex-" + Path.GetRandomFileName()[..6]);

        try
        {
            int assetId = session.Textures[0].Entry.Index;
            var texture = session.Textures[0].Texture;

            string path = AssetIo.ExportTexturePng(session, assetId, folder);

            Assert.True(File.Exists(path));
            using var image = SixLabors.ImageSharp.Image.Load(path);
            Assert.Equal(texture.Width, image.Width);
            Assert.Equal(texture.Height, image.Height);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [RomFact]
    public void ExportsACharacterAsGltfWithItsAnimation()
    {
        using var session = new RomSession(TestRom.Path!);
        string folder = Path.Combine(Path.GetTempPath(), "re2-glb-" + Path.GetRandomFileName()[..6]);

        try
        {
            // A character with a rig, so the animated path is the one exercised.
            var character = session.Characters.First(c => session.LoadPoseBank(c) is not null);

            string message = AssetIo.ExportCharacterGltf(session, character, folder, withAnimation: true);
            string path = Path.Combine(folder, $"mesh{character.MeshAssetId}.glb");

            Assert.True(File.Exists(path), message);
            Assert.Contains("joints", message);

            // Readable as glTF, rather than merely present.
            var model = SharpGLTF.Schema2.ModelRoot.Load(path);
            Assert.NotEmpty(model.LogicalMeshes);
            Assert.NotEmpty(model.LogicalAnimations);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A texture exported and re-imported must land in the project as a texture of the same shape.
    /// </summary>
    [RomFact]
    public void ImportsATextureBackIntoTheProject()
    {
        using var session = new RomSession(TestRom.Path!);
        string folder = Path.Combine(Path.GetTempPath(), "re2-round-" + Path.GetRandomFileName()[..6]);

        try
        {
            int assetId = session.Textures[0].Entry.Index;
            string png = AssetIo.ExportTexturePng(session, assetId, folder);

            var manifest = _project.Manifest!;
            var blob = manifest.Blobs.First(b => b.Ids.Contains(assetId));
            string target = Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
            byte[] before = File.ReadAllBytes(target);

            string message = AssetIo.ImportTexture(session, _project.Folder, assetId, png);
            Assert.Contains("Build ROM", message);

            byte[] after = File.ReadAllBytes(target);
            Assert.True(TextureFile.TryParse(after, out var reparsed), "the written asset is not a texture");
            Assert.Equal(session.Textures[0].Texture.Width, reparsed.Width);
            Assert.Equal(session.Textures[0].Texture.Height, reparsed.Height);

            // Put the project back so the shared fixture stays byte-identical for other tests.
            File.WriteAllBytes(target, before);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [RomFact]
    public void ReportsAProjectFolderThatCannotBeImportedInto()
    {
        Assert.False(AssetIo.HasProject(Path.GetTempPath()));
        Assert.True(AssetIo.HasProject(_project.Folder));
    }
}
