using System;
using System.IO;
using Re2.Studio;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>
/// What the editor decides about the project folder as it opens a ROM, and what it remembers between
/// runs.
/// </summary>
public sealed class StartupProjectTests
{
    private readonly ITestOutputHelper _out;

    public StartupProjectTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// With nothing remembered, the extract beside the ROM still has to be found: that is the whole
    /// point of the fallback, and it is the path every user who has not re-extracted lands on.
    /// </summary>
    [RomFact]
    public void AnExtractBesideTheRomIsFoundWithNothingRemembered()
    {
        string romDirectory = Path.GetDirectoryName(TestRom.Path)!;
        string beside = Path.Combine(romDirectory, "re2-project");

        if (!AssetIo.HasProject(beside)) return;   // nothing extracted beside the ROM to find

        // Initialise records what it settles on, so it is pointed at a throwaway settings file
        // rather than the one belonging to whoever is running the tests.
        string sandbox = Path.Combine(Path.GetTempPath(), "re2-startup-" + Guid.NewGuid().ToString("N"));
        string previous = Environment.GetEnvironmentVariable("RE2_SETTINGS_DIR") ?? "";
        Environment.SetEnvironmentVariable("RE2_SETTINGS_DIR", sandbox);

        try
        {
            Directory.CreateDirectory(sandbox);
            ProjectPanel.Initialise(TestRom.Path!, new Settings { LastProject = null });

            _out.WriteLine($"folder: {ProjectPanel.Folder}");
            Assert.True(AssetIo.HasProject(ProjectPanel.Folder),
                        $"the editor pointed at {ProjectPanel.Folder}, which holds no project");
            Assert.Equal(Path.GetFullPath(ProjectPanel.Folder), ProjectPanel.Folder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RE2_SETTINGS_DIR", previous.Length == 0 ? null : previous);
            try { Directory.Delete(sandbox, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Settings are written by more than one instance -- the Extract handler loads its own copy -- and
    /// a whole-object save let one of them write its stale nulls over the other's work.
    /// </summary>
    [Fact]
    public void SavingOneSettingDoesNotEraseAnother()
    {
        string folder = Path.Combine(Path.GetTempPath(), "re2-settings-" + Guid.NewGuid().ToString("N"));
        string previous = Environment.GetEnvironmentVariable("RE2_SETTINGS_DIR") ?? "";
        Environment.SetEnvironmentVariable("RE2_SETTINGS_DIR", folder);

        try
        {
            Directory.CreateDirectory(folder);

            // One instance records a project, exactly as the Extract handler does.
            Settings.Load().RememberProject(@"C:\somewhere\re2-project");

            // Another instance, loaded before that happened, records a ROM.
            var stale = new Settings { LastRom = null, LastProject = null };
            stale.RememberRom(@"C:\somewhere\game.z64");

            var reloaded = Settings.Load();
            _out.WriteLine($"rom={reloaded.LastRom} project={reloaded.LastProject}");

            Assert.Equal(@"C:\somewhere\game.z64", reloaded.LastRom);
            Assert.Equal(@"C:\somewhere\re2-project", reloaded.LastProject);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RE2_SETTINGS_DIR", previous.Length == 0 ? null : previous);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
