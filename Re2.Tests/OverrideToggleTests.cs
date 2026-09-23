using System.IO;
using System.Linq;
using System.Text;
using Re2.Core.Formats;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>
/// Switching between the ROM's version of an asset and the project's has to be visible in the tab that
/// shows it.
/// </summary>
public sealed class OverrideToggleTests : IClassFixture<ProjectFixture>
{
    private readonly ProjectFixture _project;

    public OverrideToggleTests(ProjectFixture project) => _project = project;

    /// <summary>Reads the text of one asset the way the Text tab does.</summary>
    private static string ReadText(RomSession session, int assetId)
        => TextTable.Read(session.Rom, session.Assets).First(e => e.AssetId == assetId).Text;

    [RomFact]
    public void EditingATextFileShowsThroughAndTheSwitchPutsItBack()
    {
        using var probe = new RomSession(TestRom.Path!);
        var entry = TextTable.Read(probe.Rom, probe.Assets).First();
        int assetId = entry.AssetId;
        string original = entry.Text;

        var blob = _project.Manifest!.Blobs.First(b => b.Ids.Contains(assetId));
        string path = Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
        byte[] before = File.ReadAllBytes(path);

        try
        {
            // Flip the case of every letter: same length, same spaces and line breaks, so the asset
            // still passes the "does this look like text" check and only its content differs.
            string edited = new string(original.Select(
                c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
            Assert.NotEqual(original, edited);

            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(edited));

            using var session = new RomSession(TestRom.Path!);
            session.Overrides.Scan(_project.Folder, session.Rom);
            session.Overrides.WaitForScan();
            Assert.Equal(1, session.Overrides.Count);

            // Showing edits: the tab reads the project's text.
            Assert.Equal(edited, ReadText(session, assetId));

            // "Use all ROM originals": the tab reads the cart's text again.
            session.Overrides.SetAllEnabled(false);
            Assert.Equal(original, ReadText(session, assetId));

            // "Use all edits": back to the project's.
            session.Overrides.SetAllEnabled(true);
            Assert.Equal(edited, ReadText(session, assetId));

            // And the master switch does the same to everything at once.
            session.Overrides.Enabled = false;
            Assert.Equal(original, ReadText(session, assetId));
        }
        finally
        {
            File.WriteAllBytes(path, before);
        }
    }

    /// <summary>
    /// Each switch has to move the version stamp, because that stamp is the only thing that tells the
    /// window to drop the panels' decoded copies.
    /// </summary>
    [RomFact]
    public void EverySwitchMovesTheVersionStamp()
    {
        using var probe = new RomSession(TestRom.Path!);
        int assetId = TextTable.Read(probe.Rom, probe.Assets).First().AssetId;

        var blob = _project.Manifest!.Blobs.First(b => b.Ids.Contains(assetId));
        string path = Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
        byte[] before = File.ReadAllBytes(path);

        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("edited"));

            using var session = new RomSession(TestRom.Path!);
            session.Overrides.Scan(_project.Folder, session.Rom);
            session.Overrides.WaitForScan();

            int version = session.Overrides.Version;

            session.Overrides.SetAllEnabled(false);
            Assert.NotEqual(version, session.Overrides.Version);

            version = session.Overrides.Version;
            session.Overrides.SetAllEnabled(true);
            Assert.NotEqual(version, session.Overrides.Version);

            version = session.Overrides.Version;
            session.Overrides.SetEnabled(assetId, false);
            Assert.NotEqual(version, session.Overrides.Version);
        }
        finally
        {
            File.WriteAllBytes(path, before);
        }
    }
}
