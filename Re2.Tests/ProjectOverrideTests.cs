using System.IO;
using System.Linq;
using Re2.Core.Formats;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>Showing project edits in the editor.</summary>
public sealed class ProjectOverrideTests : IClassFixture<ProjectFixture>
{
    private readonly ProjectFixture _project;

    public ProjectOverrideTests(ProjectFixture project) => _project = project;

    /// <summary>Edits a project file, runs the body, then puts the file back.</summary>
    private void WithEditedAsset(int assetId, byte[] replacement, System.Action<RomSession> body)
    {
        var blob = _project.Manifest!.Blobs.First(b => b.Ids.Contains(assetId));
        string path = Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
        byte[] original = File.ReadAllBytes(path);

        try
        {
            File.WriteAllBytes(path, replacement);
            using var session = new RomSession(TestRom.Path!);
            session.Overrides.Scan(_project.Folder, session.Rom);
            session.Overrides.WaitForScan();
            body(session);
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }
    }

    [RomFact]
    public void AnUneditedProjectHasNoOverrides()
    {
        using var session = new RomSession(TestRom.Path!);
        session.Overrides.Scan(_project.Folder, session.Rom);
        session.Overrides.WaitForScan();

        Assert.Equal(0, session.Overrides.Count);
        Assert.Contains("matches the ROM", session.Overrides.Message);
    }

    [RomFact]
    public void AnEditedFileIsDetectedAndServedToTheRestOfTheEditor()
    {
        using var probe = new RomSession(TestRom.Path!);
        int assetId = probe.Textures[0].Entry.Index;

        var edited = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        WithEditedAsset(assetId, edited, session =>
        {
            Assert.Equal(1, session.Overrides.Count);

            var item = session.Overrides.Items.Single();
            Assert.Equal("texture", item.Category);
            Assert.Equal(edited.Length, item.ProjectSize);

            // The point of the whole thing: reading the asset now yields the edit, not the cart.
            Assert.True(session.TryGetAsset(assetId, out var served));
            Assert.Equal(edited, served);
        });
    }

    [RomFact]
    public void TurningAnOverrideOffFallsBackToTheRom()
    {
        using var probe = new RomSession(TestRom.Path!);
        int assetId = probe.Textures[0].Entry.Index;
        Assert.True(probe.TryGetAsset(assetId, out var fromRom));

        WithEditedAsset(assetId, new byte[] { 9, 9, 9, 9 }, session =>
        {
            session.Overrides.SetEnabled(assetId, false);
            Assert.True(session.TryGetAsset(assetId, out var served));
            Assert.Equal(fromRom, served);

            session.Overrides.SetEnabled(assetId, true);
            Assert.True(session.TryGetAsset(assetId, out var again));
            Assert.Equal(new byte[] { 9, 9, 9, 9 }, again);
        });
    }

    [RomFact]
    public void TheMasterSwitchSuppressesEveryOverride()
    {
        using var probe = new RomSession(TestRom.Path!);
        int assetId = probe.Textures[0].Entry.Index;
        Assert.True(probe.TryGetAsset(assetId, out var fromRom));

        WithEditedAsset(assetId, new byte[] { 7, 7, 7, 7 }, session =>
        {
            session.Overrides.Enabled = false;
            Assert.True(session.TryGetAsset(assetId, out var served));
            Assert.Equal(fromRom, served);
        });
    }

    /// <summary>
    /// The version stamp is what tells the window to throw away decoded lists and uploaded textures.
    /// </summary>
    [RomFact]
    public void ChangingWhatIsShownMovesTheVersion()
    {
        using var session = new RomSession(TestRom.Path!);
        session.Overrides.Scan(_project.Folder, session.Rom);
        session.Overrides.WaitForScan();

        int start = session.Overrides.Version;
        session.Overrides.Enabled = false;
        Assert.NotEqual(start, session.Overrides.Version);

        int off = session.Overrides.Version;
        session.Overrides.Enabled = false;               // no change: must not churn the caches
        Assert.Equal(off, session.Overrides.Version);
    }

    /// <summary>The extract path must keep seeing the cart.</summary>
    [RomFact]
    public void ExtractStillReadsTheRomNotTheOverrides()
    {
        using var probe = new RomSession(TestRom.Path!);
        int assetId = probe.Textures[0].Entry.Index;

        WithEditedAsset(assetId, new byte[] { 3, 3, 3, 3 }, session =>
        {
            // A directory built the ordinary way has no provider attached.
            var fresh = Re2.Core.Assets.AssetDirectory.Read(session.Rom);
            var entry = fresh.Entries.First(e => e.Index == assetId);

            Assert.True(fresh.TryGetData(session.Rom, entry, out var data));
            Assert.True(TextureFile.TryParse(data, out _), "extract should still see a real texture");
        });
    }
}
