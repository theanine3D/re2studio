using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Core.Import;
using Xunit;

namespace Re2.Tests;

/// <summary>Models whose material count no longer matches the game's.</summary>
public class MaterialCountTests
{
    private static MeshFile FirstMesh(out AssetDirectory directory, out Re2.Core.Rom.RomFile rom)
    {
        rom = TestRom.Rom;
        directory = AssetDirectory.Read(rom);

        var localRom = rom;
        var localDirectory = directory;
        var entry = directory.Entries.First(e =>
            localDirectory.TryGetData(localRom, e, out var data) && MeshFile.TryParse(data, out _));

        Assert.True(directory.TryGetData(rom, entry, out var meshData));
        return MeshFile.Parse(meshData);
    }

    private static ExportTexture Solid(int size) =>
        new(ImageCodec.RgbaToPng(new byte[size * size * 4], size, size), size, size);

    /// <summary>
    /// Materials naming slots beyond the model's own are refused, and the geometry still arrives --
    /// untextured rather than dropped, so nothing is silently lost.
    /// </summary>
    [RomFact]
    public void MaterialsBeyondTheModelsSlotsAreRefusedNotWritten()
    {
        var mesh = FirstMesh(out _, out _);
        int slots = mesh.TextureCount;

        // Export with more textures than the model has slots for, which is what a user adding
        // materials in Blender produces.
        var textures = Enumerable.Range(0, slots + 3).Select(_ => Solid(8)).ToList();

        string path = Path.Combine(Path.GetTempPath(), "re2-slots-" + Path.GetRandomFileName()[..6] + ".glb");
        try
        {
            GltfExporter.Save(mesh, textures, path, "slots");

            var (model, report) = GltfImporter.Load(path, new GltfImporter.Options
            {
                PartCount = mesh.PartCount,
                TextureCount = slots
            });

            // Nothing beyond the model's own slots was bound...
            foreach (var sub in model.Parts.SelectMany(p => p.SubMeshes))
                Assert.True(sub.TextureIndex < slots,
                            $"texture index {sub.TextureIndex} would read past the model's {slots} slots");

            // ...and the geometry is still there.
            Assert.Equal(mesh.TotalTriangles, model.TotalTriangles);

            // The count the model declares is the ROM's, not the file's.
            Assert.Equal(slots, model.TextureCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>An out-of-range slot is reported as its own problem.</summary>
    [RomFact]
    public void AnOutOfRangeSlotIsReportedAsSuch()
    {
        var mesh = FirstMesh(out _, out _);
        int slots = Math.Max(1, mesh.TextureCount);

        var textures = Enumerable.Range(0, slots).Select(_ => Solid(8)).ToList();

        string path = Path.Combine(Path.GetTempPath(), "re2-slotmsg-" + Path.GetRandomFileName()[..6] + ".glb");
        try
        {
            GltfExporter.Save(mesh, textures, path, "slots");

            // Rename a material to a slot the model does not have -- what adding a material in a
            // modelling tool actually produces.
            var edited = SharpGLTF.Schema2.ModelRoot.Load(path);
            edited.LogicalMaterials[0].Name = "tex99";
            edited.SaveGLB(path);

            var (_, report) = GltfImporter.Load(path, new GltfImporter.Options
            {
                PartCount = mesh.PartCount,
                TextureCount = slots
            });

            var complaint = report.Warnings.FirstOrDefault(w => w.Contains("does not have"));
            Assert.True(complaint is not null,
                        "expected a warning about slots the model does not have, got: " +
                        string.Join(" | ", report.Warnings));

            Assert.Contains("untextured", complaint!);
            Assert.DoesNotContain("had no texNN name", complaint);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Using fewer textures than the model allows is ordinary and must not be reported as a problem:
    /// unused slots simply go unreferenced, and the declared count stays as the ROM has it.
    /// </summary>
    [RomFact]
    public void UsingFewerTexturesThanTheModelHasIsFine()
    {
        var mesh = FirstMesh(out _, out _);
        if (mesh.TextureCount < 2) return;                 // nothing to under-use

        var textures = new List<ExportTexture> { Solid(8) };

        string path = Path.Combine(Path.GetTempPath(), "re2-fewer-" + Path.GetRandomFileName()[..6] + ".glb");
        try
        {
            GltfExporter.Save(mesh, textures, path, "fewer");

            var (model, report) = GltfImporter.Load(path, new GltfImporter.Options
            {
                PartCount = mesh.PartCount,
                TextureCount = mesh.TextureCount
            });

            Assert.DoesNotContain(report.Warnings, w => w.Contains("does not have"));
            Assert.Equal(mesh.TextureCount, model.TextureCount);
            Assert.Equal(mesh.TotalTriangles, model.TotalTriangles);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// The encoded bytes are the last line of defence: whatever the file asked for, what reaches the
    /// ROM must be either a real slot or the untextured sentinel.
    /// </summary>
    [RomFact]
    public void NothingOutOfRangeSurvivesIntoTheEncodedMesh()
    {
        var mesh = FirstMesh(out _, out _);
        int slots = mesh.TextureCount;

        var textures = Enumerable.Range(0, slots + 5).Select(_ => Solid(8)).ToList();

        string path = Path.Combine(Path.GetTempPath(), "re2-enc-" + Path.GetRandomFileName()[..6] + ".glb");
        try
        {
            GltfExporter.Save(mesh, textures, path, "enc");

            var (model, _) = GltfImporter.Load(path, new GltfImporter.Options
            {
                PartCount = mesh.PartCount,
                TextureCount = slots
            });

            var bytes = MeshWriter.Write(model);
            var reread = MeshFile.Parse(bytes);            // must still be a readable model

            Assert.Equal(slots, reread.TextureCount);

            foreach (var sub in reread.Parts.SelectMany(p => p.SubMeshes))
                Assert.True(sub.TextureIndex < slots,
                            $"encoded mesh binds texture {sub.TextureIndex} of {slots}");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
