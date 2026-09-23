using System.IO;
using System.Linq;
using System.Numerics;
using System;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Core.Import;
using Re2.Studio;
using SharpGLTF.Schema2;
using Xunit;

namespace Re2.Tests;

/// <summary>A rest pose edited in a modelling tool and brought back.</summary>
public class RestPoseImportTests
{
    private static string ExportRigged(RomSession session, out int meshAssetId, out int partCount)
    {
        var character = session.Characters.First(c =>
            session.LoadMesh(c.MeshAssetId) is not null && session.LoadPoseBank(c) is not null);

        meshAssetId = character.MeshAssetId;
        partCount = session.LoadMesh(character.MeshAssetId)!.PartCount;

        string folder = Path.Combine(Path.GetTempPath(), "re2-restimp-" + Path.GetRandomFileName()[..6]);
        AssetIo.ExportCharacterGltf(session, character, folder, withAnimation: true);
        return Directory.GetFiles(folder, "*.glb").Single();
    }

    /// <summary>
    /// Every animation in an exported file is a real clip the importer can match, so importing them
    /// back reports nothing skipped.
    /// </summary>
    [RomFact]
    public void ImportingOurOwnAnimationsWarnsAboutNothing()
    {
        using var session = new RomSession(TestRom.Path!);
        string path = ExportRigged(session, out _, out _);

        try
        {
            var character = session.Characters.First(c =>
                session.LoadMesh(c.MeshAssetId) is not null && session.LoadPoseBank(c) is not null);

            var bank = session.LoadPoseBank(character)!;
            var clips = session.LoadAnimations(character);
            if (clips is null) return;

            var report = GltfAnimationImporter.Load(path, bank, clips);

            Assert.DoesNotContain(report.Warnings, w => w.Contains("skipped"));
            Assert.True(report.ClipsMatched > 0, "no clips matched, so nothing was actually imported");
        }
        finally
        {
            var folder = Path.GetDirectoryName(path)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A joint moved in a modelling tool is read back out of the file, in the game's own units.
    /// </summary>
    [RomFact]
    public void AMovedJointIsReadBackFromTheFile()
    {
        using var session = new RomSession(TestRom.Path!);
        string path = ExportRigged(session, out int meshAssetId, out _);
        string moved = path.Replace(".glb", "-moved.glb");

        try
        {
            var character = session.Characters.First(c => c.MeshAssetId == meshAssetId);
            var bank = session.LoadPoseBank(character)!;

            var model = ModelRoot.Load(path);
            var joint = model.LogicalNodes.First(n => n.Name is not null && n.Name.StartsWith("joint"));
            int index = int.Parse(joint.Name!.Substring("joint".Length));
            var startedAt = joint.LocalTransform.Translation;

            // 0.25 glTF units along X.
            joint.LocalTransform = Matrix4x4.CreateTranslation(startedAt + new Vector3(0.25f, 0, 0));
            model.SaveGLB(moved);

            var read = GltfImporter.LoadRestPose(moved, bank.PartCount);

            Assert.True(read.ContainsKey(index), $"joint {index} was not read back");
            var (x, y, z) = read[index];
            var original = bank.Joints[index];

            Assert.Equal(original.X, (short)x);
            Assert.Equal(original.Y, (short)y);
            Assert.Equal(original.Z + 250, z);
        }
        finally
        {
            var folder = Path.GetDirectoryName(path)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Writing the rest pose into a bank and reading it back must give the same joints, through the
    /// halfword-swapped storage the game uses.
    /// </summary>
    [RomFact]
    public void WrittenRestOffsetsReadBackIdentically()
    {
        using var session = new RomSession(TestRom.Path!);
        var character = session.Characters.First(c => session.LoadPoseBank(c) is not null);
        var bank = session.LoadPoseBank(character)!;

        var wanted = new (int X, int Y, int Z)[bank.PartCount];
        for (int p = 0; p < bank.PartCount; p++)
            wanted[p] = (bank.Joints[p].X + p, bank.Joints[p].Y - p, bank.Joints[p].Z + 2 * p);

        Assert.Equal(0, PoseBankWriter.WriteRestOffsets(bank, wanted));

        var reread = PoseBank.Parse(bank.Data);
        for (int p = 0; p < bank.PartCount; p++)
        {
            Assert.Equal(wanted[p].X, reread.Joints[p].X);
            Assert.Equal(wanted[p].Y, reread.Joints[p].Y);
            Assert.Equal(wanted[p].Z, reread.Joints[p].Z);
        }
    }

    /// <summary>
    /// The whole point: a moved joint reaches the project, and the geometry stays put relative to it
    /// rather than being displaced by the same amount twice.
    /// </summary>
    [RomFact]
    public void ImportingAMovedSkeletonUpdatesTheJointsAndNotTheGeometry()
    {
        using var session = new RomSession(TestRom.Path!);
        string path = ExportRigged(session, out int meshAssetId, out _);
        string moved = path.Replace(".glb", "-moved.glb");

        var character = session.Characters.First(c => c.MeshAssetId == meshAssetId);
        var bank = session.LoadPoseBank(character)!;
        var existing = session.LoadMesh(meshAssetId)!;

        try
        {
            var options = new GltfImporter.Options
            {
                PartOffsets = bank.RestWorldPositions(),
                PartCount = existing.PartCount,
                TextureCount = existing.TextureCount
            };

            var (before, _) = GltfImporter.Load(path, options);

            var model = ModelRoot.Load(path);
            var joint = model.LogicalNodes.First(n => n.Name is not null && n.Name.StartsWith("joint"));
            joint.LocalTransform = Matrix4x4.CreateTranslation(
                joint.LocalTransform.Translation + new Vector3(0.25f, 0, 0));
            model.SaveGLB(moved);

            // Import the moved file the way AssetIo does: offsets taken from the file's own skeleton.
            var edited = GltfImporter.LoadRestPose(moved, bank.PartCount);
            var locals = new (int X, int Y, int Z)[bank.PartCount];
            for (int p = 0; p < bank.PartCount; p++)
                locals[p] = edited.TryGetValue(p, out var e)
                    ? e
                    : (bank.Joints[p].X, bank.Joints[p].Y, bank.Joints[p].Z);

            var movedOptions = new GltfImporter.Options
            {
                PartOffsets = bank.WorldPositionsFrom(locals),
                PartCount = existing.PartCount,
                TextureCount = existing.TextureCount
            };

            var (after, _) = GltfImporter.Load(moved, movedOptions);

            // Measuring against the joint's new home leaves the local geometry where it was: the
            // limb moved because its joint moved, not because its vertices were rewritten.
            var beforeVerts = before.Parts.SelectMany(p => p.SubMeshes).SelectMany(s => s.Vertices).ToList();
            var afterVerts = after.Parts.SelectMany(p => p.SubMeshes).SelectMany(s => s.Vertices).ToList();
            Assert.Equal(beforeVerts.Count, afterVerts.Count);

            int drifted = beforeVerts.Zip(afterVerts).Count(pair =>
                Math.Abs(pair.First.X - pair.Second.X) > 1 ||
                Math.Abs(pair.First.Y - pair.Second.Y) > 1 ||
                Math.Abs(pair.First.Z - pair.Second.Z) > 1);

            Assert.True(drifted == 0,
                        $"{drifted} of {beforeVerts.Count} vertices moved; the offset was applied twice");
        }
        finally
        {
            var folder = Path.GetDirectoryName(path)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
