using System;
using System.IO;
using System.Threading;
using Re2.Core.Rom;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>How the editor finds out that the project folder has changed.</summary>
public sealed class ProjectRescanTests
{
    private static string NewFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "re2-rescan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A ROM-shaped blob: the scan only needs something to hand to the decoder.</summary>
    private static RomFile EmptyRom()
    {
        var data = new byte[1024];
        data[0] = 0x80; data[1] = 0x37; data[2] = 0x12; data[3] = 0x40;   // big-endian .z64 magic
        return RomFile.FromBytes(data);
    }

    [Fact]
    public void AnEditMadeByTheEditorAsksForARescanAtOnce()
    {
        using var overrides = new ProjectOverrides();

        Assert.False(overrides.NeedsRescan, "nothing has changed yet");

        overrides.NoteEdited();

        // No sleeping: an import must not be invisible for the debounce interval.
        Assert.True(overrides.NeedsRescan, "the editor's own write did not ask for a rescan");
    }

    [Fact]
    public void AScanClearsThePendingMarkOnceItHasStarted()
    {
        string folder = NewFolder();
        try
        {
            using var overrides = new ProjectOverrides();
            var rom = EmptyRom();

            overrides.NoteEdited();
            Assert.True(overrides.NeedsRescan);

            overrides.Scan(folder, rom);
            overrides.WaitForScan();

            Assert.False(overrides.NeedsRescan, "the mark survived a completed scan");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The regression itself: a change that arrives while a scan is running must still be serviced.
    /// </summary>
    [Fact]
    public void AChangeArrivingDuringAScanIsNotLost()
    {
        string folder = NewFolder();
        try
        {
            using var overrides = new ProjectOverrides();
            var rom = EmptyRom();

            overrides.Scan(folder, rom);

            // Whatever the running scan's fate, a fresh edit and a Scan call that cannot start
            // (because one may still be in flight) must leave the request standing.
            overrides.NoteEdited();
            overrides.Scan(folder, rom);

            if (overrides.Scanning)
                Assert.True(overrides.NeedsRescan, "the pending change was dropped by a scan that never started");

            overrides.WaitForScan();

            // And once things are quiet, asking again actually services it.
            overrides.NoteEdited();
            Assert.True(overrides.NeedsRescan);
            overrides.Scan(folder, rom);
            overrides.WaitForScan();
            Assert.False(overrides.NeedsRescan);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ScanningRaisesTheVersionSoCachedDecodesAreDropped()
    {
        string folder = NewFolder();
        try
        {
            using var overrides = new ProjectOverrides();
            var rom = EmptyRom();

            int before = overrides.Version;

            overrides.Scan(folder, rom);
            overrides.WaitForScan();

            Assert.True(overrides.Version > before,
                        "the version did not move, so the panels would keep their stale copies");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
