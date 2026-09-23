using System;
using System.IO;
using System.Linq;
using Re2.Core.Project;
using Re2.Core.Rom;
using Re2.Studio;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>
/// Finding edits that are already sitting in a project folder when the editor starts.
/// </summary>
public sealed class ProjectDetectionTests
{
    private readonly ITestOutputHelper _out;

    public ProjectDetectionTests(ITestOutputHelper output) => _out = output;

    [RomFact]
    public void AnExtractWithEditsInItIsSeenOnOpening()
    {
        var rom = TestRom.Rom;

        string folder = Path.Combine(Path.GetTempPath(), "re2-detect-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProjectFolder.Extract(rom, folder, (_, _) => { });

            var (ready, message) = AssetIo.DescribeProject(folder);
            Assert.True(ready, $"a fresh extract was not usable: {message}");

            // Edit one asset the way a user would: change the file on disk, nothing else.
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ProjectManifest>(
                File.ReadAllText(Path.Combine(folder, ProjectFolder.ManifestName)))!;
            var blob = manifest.Blobs.First(b => b.Category == "model");
            string path = Path.Combine(folder, blob.File.Replace('/', Path.DirectorySeparatorChar));

            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            using var overrides = new ProjectOverrides();
            overrides.Scan(folder, rom);
            overrides.WaitForScan();

            _out.WriteLine($"status: {overrides.Message}");
            _out.WriteLine($"items:  {overrides.Items.Count}");

            Assert.True(overrides.Items.Count > 0,
                        $"the edit in the project was not detected; the tab would say: {overrides.Message}");
            Assert.Contains(overrides.Items, i => i.Ids.Contains(blob.Ids[0]));
            Assert.NotNull(overrides.Provide(blob.Ids[0]));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The user's own project, when it is present.</summary>
    [RomFact]
    public void TheProjectBesideTheRomIsReadableWhenItExists()
    {
        var rom = TestRom.Rom;
        string folder = Path.Combine(Path.GetDirectoryName(TestRom.Path)!, "re2-project");

        if (!AssetIo.HasProject(folder)) return;   // nothing extracted there; nothing to check

        var (ready, message) = AssetIo.DescribeProject(folder);
        _out.WriteLine($"describe: ready={ready} {message}");
        Assert.True(ready, $"the project beside the ROM was rejected: {message}");

        using var overrides = new ProjectOverrides();
        overrides.Scan(folder, rom);
        overrides.WaitForScan();

        _out.WriteLine($"status: {overrides.Message}");
        foreach (var item in overrides.Items)
            _out.WriteLine($"  {item.AssetId,5} {item.Category,-10} {item.File}");
    }
}
