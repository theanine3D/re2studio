using System;
using System.IO;
using System.Linq;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Core.Import;
using Re2.Studio;
using SharpGLTF.Schema2;
using Xunit;

namespace Re2.Tests;

/// <summary>
/// Characters export as a skinned mesh, so a modelling tool builds a real armature rather than a tree
/// of parented objects.
/// </summary>
public class SkinnedExportTests
{
    private static (string Path, CharacterEntryHolder Character) Export(RomSession session)
    {
        var character = session.Characters.First(c =>
            session.LoadMesh(c.MeshAssetId) is not null && session.LoadPoseBank(c) is not null);

        string folder = Path.Combine(Path.GetTempPath(), "re2-skin-" + Path.GetRandomFileName()[..6]);
        AssetIo.ExportCharacterGltf(session, character, folder, withAnimation: true);

        return (Directory.GetFiles(folder, "*.glb").Single(), new CharacterEntryHolder(character));
    }

    /// <summary>Keeps the character alongside its file without repeating the search.</summary>
    public sealed record CharacterEntryHolder(Re2.Core.Assets.ModelEntry Entry);

    [RomFact]
    public void TheExportHasASkinWithOneJointPerPart()
    {
        using var session = new RomSession(TestRom.Path!);
        var (path, holder) = Export(session);

        try
        {
            var model = ModelRoot.Load(path);

            Assert.NotEmpty(model.LogicalSkins);

            var skin = model.LogicalSkins[0];
            var bank = session.LoadPoseBank(holder.Entry)!;
            var mesh = session.LoadMesh(holder.Entry.MeshAssetId)!;

            // A joint for every part, including any the rig does not animate -- those still hold
            // real geometry and need somewhere to be bound.
            Assert.Equal(Math.Max(mesh.PartCount, bank.PartCount), skin.JointsCount);

            // Every vertex must be fully bound, or the game -- which moves parts rigidly -- cannot
            // represent it.
            foreach (var primitive in model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                Assert.NotNull(weights);

                foreach (var w in weights!)
                {
                    float total = w.X + w.Y + w.Z + w.W;
                    float largest = MathF.Max(MathF.Max(w.X, w.Y), MathF.Max(w.Z, w.W));
                    Assert.Equal(1f, total, 1e-4f);
                    Assert.Equal(1f, largest, 1e-4f);      // all of it on one joint
                }
            }
        }
        finally
        {
            var folder = Path.GetDirectoryName(path)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The round trip that matters: export a character, read it straight back, and get the same
    /// geometry in the same parts. Part identity now comes from the weights rather than node names.
    /// </summary>
    [RomFact]
    public void GeometrySurvivesTheSkinnedRoundTrip()
    {
        using var session = new RomSession(TestRom.Path!);
        var (path, holder) = Export(session);

        try
        {
            var original = session.LoadMesh(holder.Entry.MeshAssetId)!;
            var bank = session.LoadPoseBank(holder.Entry)!;

            var (model, report) = GltfImporter.Load(path, new GltfImporter.Options
            {
                PartOffsets = bank.RestWorldPositions(),
                PartCount = original.PartCount,
                TextureCount = original.TextureCount
            });

            Assert.Empty(report.Warnings.Where(w => w.Contains("weighted to more than one")));
            Assert.Equal(original.TotalTriangles, model.TotalTriangles);

            // Same parts carry geometry, and each carries the same number of triangles.
            for (int part = 0; part < original.PartCount; part++)
            {
                int before = original.Parts[part].SubMeshes.Sum(sm => sm.Triangles.Count);
                int after = model.Parts[part].SubMeshes.Sum(sm => sm.Triangles.Count);
                Assert.True(before == after,
                            $"part {part}: {before} triangles exported, {after} came back");
            }
        }
        finally
        {
            var folder = Path.GetDirectoryName(path)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Vertex positions have to come back where they started, or a straight export/import would
    /// quietly move the model.
    /// </summary>
    [RomFact]
    public void VertexPositionsSurviveTheSkinnedRoundTrip()
    {
        using var session = new RomSession(TestRom.Path!);
        var (path, holder) = Export(session);

        try
        {
            var original = session.LoadMesh(holder.Entry.MeshAssetId)!;
            var bank = session.LoadPoseBank(holder.Entry)!;

            var (model, _) = GltfImporter.Load(path, new GltfImporter.Options
            {
                PartOffsets = bank.RestWorldPositions(),
                PartCount = original.PartCount,
                TextureCount = original.TextureCount
            });

            for (int part = 0; part < original.PartCount; part++)
            {
                var before = original.Parts[part].SubMeshes.SelectMany(sm => sm.Vertices).ToList();
                var after = model.Parts[part].SubMeshes.SelectMany(sm => sm.Vertices).ToList();
                if (before.Count == 0 || before.Count != after.Count) continue;

                // Order is not guaranteed through the round trip, so compare the extents.
                foreach (var axis in new[] { 0, 1, 2 })
                {
                    int Component(MeshVertex v) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

                    Assert.True(Math.Abs(before.Min(Component) - after.Min(Component)) <= 1,
                                $"part {part} axis {axis}: minimum moved");
                    Assert.True(Math.Abs(before.Max(Component) - after.Max(Component)) <= 1,
                                $"part {part} axis {axis}: maximum moved");
                }
            }
        }
        finally
        {
            var folder = Path.GetDirectoryName(path)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
