using System;
using System.IO;
using System.Linq;
using Re2.Core.Project;
using Re2.Core.Rom;
using Re2.Studio;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Re-extracting over a project that the editor is already watching.</summary>
public sealed class ReExtractTests
{
    private readonly ITestOutputHelper _out;

    public ReExtractTests(ITestOutputHelper output) => _out = output;

    private static RomFile EmptyRom()
    {
        var data = new byte[1024];
        data[0] = 0x80; data[1] = 0x37; data[2] = 0x12; data[3] = 0x40;
        return RomFile.FromBytes(data);
    }

    [Fact]
    public void PausingStopsScanningAndWaitsForAScanAlreadyRunning()
    {
        string folder = Path.Combine(Path.GetTempPath(), "re2-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            using var overrides = new ProjectOverrides();
            var rom = EmptyRom();

            overrides.Scan(folder, rom);

            using (overrides.Pause())
            {
                Assert.False(overrides.Scanning, "a scan was still running after Pause returned");

                // While paused nothing may start reading the folder again.
                overrides.Scan(folder, rom);
                Assert.False(overrides.Scanning, "Pause did not stop a new scan from starting");
            }

            // And once released, the folder is known to have changed underneath it.
            Assert.True(overrides.NeedsRescan, "the folder was not reread after the pause ended");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The reported failure, end to end: extract, let the editor scan it, then extract again over the
    /// top.
    /// </summary>
    [RomFact]
    public void AProjectCanBeReExtractedWhileTheEditorIsWatchingIt()
    {
        var rom = TestRom.Rom;
        string folder = Path.Combine(Path.GetTempPath(), "re2-reextract-" + Guid.NewGuid().ToString("N"));

        try
        {
            ProjectFolder.Extract(rom, folder, (_, _) => { });

            using var overrides = new ProjectOverrides();
            overrides.Scan(folder, rom);          // the editor starts watching, as it does on opening

            // Re-extract over the top while that is in flight, exactly as the Extract button does.
            using (overrides.Pause())
            {
                var manifest = ProjectFolder.Extract(rom, folder, (_, _) => { });
                Assert.True(manifest.Blobs.Count > 7000);
            }

            overrides.Scan(folder, rom);
            overrides.WaitForScan();

            _out.WriteLine($"after re-extract: {overrides.Message}");
            Assert.Empty(overrides.Items);        // a fresh extract matches the ROM in every file
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
