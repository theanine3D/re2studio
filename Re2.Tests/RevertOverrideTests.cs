using System.IO;
using System.Linq;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>
/// Reverting an override: the project file goes back to the ROM's own copy and the override stops
/// existing.
/// </summary>
public sealed class RevertOverrideTests : IClassFixture<ProjectFixture>
{
    private readonly ProjectFixture _project;

    public RevertOverrideTests(ProjectFixture project) => _project = project;

    private string FileFor(int assetId)
    {
        var blob = _project.Manifest!.Blobs.First(b => b.Ids.Contains(assetId));
        return Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
    }

    [RomFact]
    public void RevertingRestoresTheRomsBytesAndClearsTheOverride()
    {
        using var session = new RomSession(TestRom.Path!);
        int assetId = session.Textures[0].Entry.Index;

        string path = FileFor(assetId);
        byte[] original = File.ReadAllBytes(path);

        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });

            session.Overrides.Scan(_project.Folder, session.Rom);
            session.Overrides.WaitForScan();
            Assert.Equal(1, session.Overrides.Count);

            string report = AssetIo.RevertOverride(session, _project.Folder, assetId);
            Assert.Contains("reverted", report);

            // The file is the ROM's copy again, byte for byte.
            Assert.Equal(original, File.ReadAllBytes(path));

            // And the override is gone, not merely hidden.
            session.Overrides.Scan(_project.Folder, session.Rom);
            session.Overrides.WaitForScan();
            Assert.Equal(0, session.Overrides.Count);
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }
    }

    /// <summary>The override has to be read past, not through.</summary>
    [RomFact]
    public void RevertingIgnoresTheOverrideItIsDiscarding()
    {
        using var session = new RomSession(TestRom.Path!);
        int assetId = session.Textures[0].Entry.Index;

        string path = FileFor(assetId);
        byte[] original = File.ReadAllBytes(path);
        var edited = new byte[] { 9, 8, 7, 6 };

        try
        {
            File.WriteAllBytes(path, edited);
            session.Overrides.Scan(_project.Folder, session.Rom);
            session.Overrides.WaitForScan();

            // The session is serving the edit at this point...
            Assert.True(session.TryGetAsset(assetId, out var served));
            Assert.Equal(edited, served);

            // ...and the revert must still write the ROM's bytes, not those.
            AssetIo.RevertOverride(session, _project.Folder, assetId);

            var written = File.ReadAllBytes(path);
            Assert.NotEqual(edited, written);
            Assert.Equal(original, written);
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }
    }

    [RomFact]
    public void RevertingAnAssetTheProjectDoesNotHaveIsRefused()
    {
        using var session = new RomSession(TestRom.Path!);

        var error = Assert.Throws<System.IO.InvalidDataException>(
            () => AssetIo.RevertOverride(session, _project.Folder, 999999));

        Assert.Contains("not in the project", error.Message);
    }
}
