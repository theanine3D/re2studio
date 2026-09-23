using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Core.Import;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

public sealed class GltfImporterTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _scratch;

    public GltfImporterTests(ITestOutputHelper output)
    {
        _output = output;
        _scratch = Path.Combine(Path.GetTempPath(), "re2-gltf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    private static List<MeshFile> LoadMeshes(int take)
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var meshes = new List<MeshFile>();

        foreach (var entry in directory.Entries)
        {
            if (meshes.Count >= take) break;
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (MeshFile.TryParse(data, out var mesh) && mesh.TotalTriangles > 50) meshes.Add(mesh);
        }

        return meshes;
    }

    /// <summary>The three positions a triangle names, as plain integers.</summary>
    private static List<(int, int, int, int, int, int, int, int, int)> Geometry(MeshFile mesh)
    {
        var result = new List<(int, int, int, int, int, int, int, int, int)>();
        foreach (var part in mesh.Parts)
        foreach (var sub in part.SubMeshes)
        foreach (var t in sub.Triangles)
        {
            var a = sub.Vertices[t.A]; var b = sub.Vertices[t.B]; var c = sub.Vertices[t.C];
            if (SamePosition(a, b) || SamePosition(b, c) || SamePosition(a, c)) continue;
            result.Add((a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z));
        }
        result.Sort();
        return result;
    }

    private static bool SamePosition(MeshVertex a, MeshVertex b)
        => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

    /// <summary>
    /// The end-to-end claim the feature rests on: export a model, import it back, and get the same
    /// geometry.
    /// </summary>
    [RomFact]
    public void ExportedModelsImportBackWithTheSameGeometry()
    {
        var meshes = LoadMeshes(12);
        Assert.NotEmpty(meshes);

        int worst = 0, collapsed = 0;
        foreach (var (mesh, i) in meshes.Select((m, i) => (m, i)))
        {
            string path = Path.Combine(_scratch, $"mesh{i}.glb");
            GltfExporter.Save(mesh, path, "mesh");

            var (model, report) = GltfImporter.Load(path, new GltfImporter.Options
            {
                PartCount = mesh.PartCount,
                TextureCount = mesh.TextureCount
            });

            var rebuilt = MeshFile.Parse(MeshWriter.Write(model));

            Assert.Equal(mesh.PartCount, rebuilt.PartCount);
            Assert.Equal(report.PartsMatchedByName, model.Parts.Count(p => p.SubMeshes.Count > 0));

            var before = Geometry(mesh);
            var after = Geometry(rebuilt);

            // Every triangle that can be represented must come back, and no extras invented.
            Assert.Equal(before.Count, after.Count);
            collapsed += mesh.TotalTriangles - before.Count;

            for (int t = 0; t < before.Count; t++)
            {
                var x = before[t]; var y = after[t];
                foreach (var (p, q) in new[]
                         {
                             (x.Item1, y.Item1), (x.Item2, y.Item2), (x.Item3, y.Item3),
                             (x.Item4, y.Item4), (x.Item5, y.Item5), (x.Item6, y.Item6),
                             (x.Item7, y.Item7), (x.Item8, y.Item8), (x.Item9, y.Item9)
                         })
                    worst = Math.Max(worst, Math.Abs(p - q));
            }
        }

        _output.WriteLine($"{meshes.Count} models round-tripped through glTF; " +
                          $"worst coordinate drift {worst} unit(s); " +
                          $"{collapsed} zero-area triangle(s) could not survive vertex dedup");
        Assert.True(worst <= 1, $"positions moved by up to {worst} units through the round trip");
    }

    /// <summary>Part indices must survive, or the animation drives the wrong limbs.</summary>
    [Fact]
    public void PartIndicesSurviveIncludingEmptySlots()
    {
        var source = new MeshBuildModel { TextureCount = 4 };
        for (int i = 0; i < 8; i++) source.Parts.Add(new MeshBuildPart());

        // Populate 1, 2, 5 and 7, leaving deliberate gaps at 0, 3, 4 and 6.
        foreach (int p in new[] { 1, 2, 5, 7 })
            source.Parts[p].SubMeshes.Add(Wedge(p));

        var written = MeshFile.Parse(MeshWriter.Write(source));

        // Export with a stand-in texture list so materials carry their texNN names; without it the
        // importer rightly warns that it cannot tell which texture each primitive wanted.
        var stand = Enumerable.Range(0, 4).Select(_ => new ExportTexture(Array.Empty<byte>(), 1, 1)).ToList();
        string path = Path.Combine(_scratch, "gaps.glb");
        GltfExporter.Save(written, stand, path, "mesh");

        var (model, report) = GltfImporter.Load(path, new GltfImporter.Options
        {
            PartCount = written.PartCount,
            TextureCount = written.TextureCount
        });

        Assert.Equal(4, report.PartsMatchedByName);
        Assert.Empty(report.Warnings);
        Assert.Equal(0, report.UnmatchedMaterials);
        Assert.Equal(8, model.Parts.Count);

        for (int p = 0; p < 8; p++)
        {
            bool expected = p is 1 or 2 or 5 or 7;
            Assert.Equal(expected, model.Parts[p].SubMeshes.Count > 0);
        }

        // And the geometry landed in the right slot, not merely in some slot.
        var rebuilt = MeshFile.Parse(MeshWriter.Write(model));
        for (int p = 0; p < 8; p++)
            if (rebuilt.Parts[p].SubMeshes.Count > 0)
                Assert.Equal(p * 10, rebuilt.Parts[p].SubMeshes[0].Vertices[0].X);
    }

    /// <summary>A distinct little triangle whose first vertex encodes which part it belongs to.</summary>
    private static MeshBuildSubMesh Wedge(int part)
    {
        var sub = new MeshBuildSubMesh { TextureIndex = part % 4 };
        sub.Vertices.Add(new MeshVertex((short)(part * 10), 0, 0, 0, 0, 0, 127, 0, 255));
        sub.Vertices.Add(new MeshVertex((short)(part * 10 + 5), 40, 0, 8000, 0, 0, 127, 0, 255));
        sub.Vertices.Add(new MeshVertex((short)(part * 10), 40, 30, 0, 8000, 0, 127, 0, 255));
        sub.Triangles.Add(new MeshTriangle(0, 1, 2));
        return sub;
    }

    /// <summary>Material names carry the texture binding; anything else means untextured.</summary>
    [RomFact]
    public void MaterialNamesBindTextures()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        MeshFile? textured = null;

        foreach (var entry in directory.Entries)
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var m)) continue;
            if (m.Parts.Any(p => p.SubMeshes.Any(s => s.TextureIndex > 0))) { textured = m; break; }
        }

        Assert.NotNull(textured);

        // Export with textures so the materials get their texNN names.
        var stand = Enumerable.Range(0, Math.Max(1, textured!.TextureCount))
            .Select(_ => new ExportTexture(Array.Empty<byte>(), 1, 1))
            .ToList();

        string path = Path.Combine(_scratch, "textured.glb");
        GltfExporter.Save(textured, stand, path, "mesh");

        var (model, _) = GltfImporter.Load(path, new GltfImporter.Options
        {
            PartCount = textured.PartCount,
            TextureCount = textured.TextureCount
        });

        var expected = textured.Parts.SelectMany(p => p.SubMeshes).Select(s => s.TextureIndex).Where(i => i >= 0).ToHashSet();
        var actual = model.Parts.SelectMany(p => p.SubMeshes).Select(s => s.TextureIndex).Where(i => i >= 0).ToHashSet();

        Assert.NotEmpty(actual);
        Assert.Subset(expected, actual);
    }

    /// <summary>A mesh far larger than one G_VTX load must import and re-read intact.</summary>
    [RomFact]
    public void ALargeModelImportsWithoutLosingTriangles()
    {
        var big = LoadMeshes(60).OrderByDescending(m => m.TotalTriangles).First();
        _output.WriteLine($"largest sampled model: {big.TotalVertices:N0} vertices, {big.TotalTriangles:N0} triangles");

        string path = Path.Combine(_scratch, "big.glb");
        GltfExporter.Save(big, path, "mesh");

        var (model, _) = GltfImporter.Load(path, new GltfImporter.Options
        {
            PartCount = big.PartCount,
            TextureCount = big.TextureCount
        });

        var rebuilt = MeshFile.Parse(MeshWriter.Write(model));
        Assert.Equal(big.TotalTriangles, rebuilt.TotalTriangles);

        // No sub-mesh may claim more vertices than the buffer can address in one load unless the
        // display list actually splits it, which the reader would catch as dropped triangles.
        Assert.All(rebuilt.Parts.SelectMany(p => p.SubMeshes),
                   s => Assert.True(s.Triangles.Count > 0, "a sub-mesh came back with no triangles"));
    }


    /// <summary>
    /// The rigged round trip, which is the one users will actually do: export a character with
    /// "characters --export", edit it, import it back.
    /// </summary>
    [RomFact]
    public void RiggedRoundTripKeepsEveryPartWhereItWas()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        MeshFile? mesh = null;
        PoseBank? bank = null;

        for (int table = 0; table < 4 && mesh is null; table++)
        foreach (var character in ModelTextureTable.ReadCharacters(rom, table))
        {
            if (character.AnimationAssetIds.Count < 2) continue;

            var entry = directory.Entries.FirstOrDefault(e => e.Index == character.MeshAssetId);
            if (entry is null || !directory.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var candidate)) continue;

            PoseBank candidateBank;
            try { candidateBank = PoseBank.Assemble(rom, directory, character.AnimationAssetIds[1]); }
            catch (Exception) { continue; }

            // Specifically want the awkward case: more mesh parts than rig joints.
            if (candidate.PartCount <= candidateBank.PartCount) continue;

            mesh = candidate;
            bank = candidateBank;
            break;
        }

        Assert.True(mesh is not null, "no character with more mesh parts than rig joints was found");
        _output.WriteLine($"{mesh!.PartCount} mesh parts against {bank!.PartCount} rig joints");

        string path = Path.Combine(_scratch, "rigged.glb");
        AnimatedGltfExporter.Save(mesh, bank, null, null, path, "char");

        var (model, report) = GltfImporter.Load(path, new GltfImporter.Options
        {
            PartOffsets = bank.RestWorldPositions(),
            PartCount = mesh.PartCount,
            TextureCount = mesh.TextureCount
        });

        var rebuilt = MeshFile.Parse(MeshWriter.Write(model));
        Assert.Equal(mesh.PartCount, rebuilt.PartCount);

        // Every part that had geometry must still have it, in the same place.
        int compared = 0;
        for (int p = 0; p < mesh.PartCount; p++)
        {
            var before = PartPositions(mesh.Parts[p]);
            var after = PartPositions(rebuilt.Parts[p]);
            if (before.Count == 0) continue;

            Assert.True(after.Count > 0, $"part {p} lost all its geometry");

            // Compare the extremes: a mis-cancelled offset shifts the whole part bodily.
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.InRange(after.Min(v => Axis(v, axis)) - before.Min(v => Axis(v, axis)), -1, 1);
                Assert.InRange(after.Max(v => Axis(v, axis)) - before.Max(v => Axis(v, axis)), -1, 1);
            }
            compared++;
        }

        Assert.True(compared >= mesh.PartCount - 2, $"only {compared} parts carried geometry to compare");
        Assert.Equal(compared, report.PartsMatchedByName);
    }

    private static List<MeshVertex> PartPositions(MeshPart part)
        => part.SubMeshes.SelectMany(s => s.Vertices).ToList();

    private static int Axis(MeshVertex v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    [RomFact]
    public void MissingFileIsReportedClearly()
    {
        Assert.ThrowsAny<Exception>(() =>
            GltfImporter.Load(Path.Combine(_scratch, "nope.glb"), new GltfImporter.Options()));
    }
}
