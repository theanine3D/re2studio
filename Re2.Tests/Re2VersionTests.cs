using System.Collections.Generic;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

/// <summary>Both USA builds have to edit the same way.</summary>
public sealed class Re2VersionTests
{
    private static string Rev0Path => BothRoms.Rev0!;
    private static string Rev1Path => BothRoms.Rev1!;

    private static (RomFile Rev0, RomFile Rev1) Load() => (RomFile.Load(Rev0Path), RomFile.Load(Rev1Path));

    [BothRomsFact]
    public void EachBuildIsIdentified()
    {
        var (rev0, rev1) = Load();

        Assert.Equal(Re2Release.UsaRev0, Re2Version.Detect(rev0).Release);
        Assert.Equal(Re2Release.UsaRev1, Re2Version.Detect(rev1).Release);
        Assert.Equal(-96, Re2Version.Detect(rev0).MainOverlayDelta);
        Assert.Equal(0, Re2Version.Detect(rev1).MainOverlayDelta);
    }

    /// <summary>An edited ROM has a new checksum, so the header's version byte has to carry it.</summary>
    [BothRomsFact]
    public void AnEditedRomIsStillIdentified()
    {
        var (rev0, rev1) = Load();

        rev0.Crc1 = 0x12345678;
        rev1.Crc1 = 0x12345678;

        Assert.Equal(Re2Release.UsaRev0, Re2Version.Detect(rev0).Release);
        Assert.Equal(Re2Release.UsaRev1, Re2Version.Detect(rev1).Release);
    }

    [BothRomsFact]
    public void TheCodeTablesReadTheSameInBothBuilds()
    {
        var (rev0, rev1) = Load();

        var o0 = ModelTextureTable.LoadMainOverlay(rev0);
        var o1 = ModelTextureTable.LoadMainOverlay(rev1);

        for (int table = 0; table < ModelTextureTable.EntityTableAddresses.Length; table++)
        {
            var c0 = ModelTextureTable.ReadCharacters(o0, table);
            var c1 = ModelTextureTable.ReadCharacters(o1, table);

            Assert.NotEmpty(c1);
            Assert.Equal(c1.Select(c => (c.Index, c.MeshAssetId, c.PairIndex)),
                         c0.Select(c => (c.Index, c.MeshAssetId, c.PairIndex)));
        }

        Assert.Equal(ItemTable.Read(o1).Select(i => i.MeshAssetId),
                     ItemTable.Read(o0).Select(i => i.MeshAssetId));

        var rooms0 = RoomTable.Read(o0);
        var rooms1 = RoomTable.Read(o1);
        Assert.NotEmpty(rooms1);
        Assert.Equal(rooms1.Select(r => (r.Stage, r.Room, r.ViewCount)),
                     rooms0.Select(r => (r.Stage, r.Room, r.ViewCount)));

        var masks0 = MaskTable.Read(o0);
        var masks1 = MaskTable.Read(o1);
        Assert.NotEmpty(masks1);
        Assert.Equal(masks1.Select(m => m.Length), masks0.Select(m => m.Length));

        var models = new HashSet<int>(ModelTextureTable.ReadCharacters(o1).Select(c => c.MeshAssetId));
        Assert.Equal(RoomPropTable.ReadAll(o1, models).Count, RoomPropTable.ReadAll(o0, models).Count);
    }

    /// <summary>
    /// A project belongs to the build it came from, and says so, so building it onto the other one
    /// warns rather than quietly producing a mixture of the two.
    /// </summary>
    [BothRomsFact]
    public void BuildingAProjectOntoTheOtherBuildWarns()
    {
        var (rev0, rev1) = Load();
        var manifest = new Re2.Core.Project.ProjectManifest { Release = Re2Release.UsaRev0.ToString() };

        Assert.Null(Re2.Core.Project.ProjectFolder.ReleaseMismatch(manifest, rev0));

        string? warning = Re2.Core.Project.ProjectFolder.ReleaseMismatch(manifest, rev1);
        Assert.NotNull(warning);
        Assert.Contains("Rev 1", warning);

        // A project from before the field was recorded says nothing either way.
        Assert.Null(Re2.Core.Project.ProjectFolder.ReleaseMismatch(
            new Re2.Core.Project.ProjectManifest(), rev1));
    }

    /// <summary>Extract and rebuild has to reproduce either build byte for byte.</summary>
    [BothRomsFact]
    public void EachBuildRebuildsByteIdentically()
    {

        foreach (string path in new[] { Rev0Path, Rev1Path })
        {
            string folder = Path.Combine(Path.GetTempPath(), "re2-version-" + Path.GetFileNameWithoutExtension(path));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);

            try
            {
                var rom = RomFile.Load(path);
                Re2.Core.Project.ProjectFolder.Extract(rom, folder);
                var result = Re2.Core.Project.ProjectFolder.Build(rom, folder, RomFile.Load(path));

                Assert.True(result.ByteIdentical, $"{Path.GetFileName(path)} did not rebuild identically");
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
