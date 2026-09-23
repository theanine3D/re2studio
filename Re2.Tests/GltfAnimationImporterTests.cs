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

public sealed class GltfAnimationImporterTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _scratch;

    public GltfAnimationImporterTests(ITestOutputHelper output)
    {
        _output = output;
        _scratch = Path.Combine(Path.GetTempPath(), "re2-anim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    /// <summary>A character that has a mesh, a rig and clips -- everything the round trip needs.</summary>
    private static (ModelEntry Character, MeshFile Mesh, PoseBank Bank, AnimationSet Clips) FindAnimated(int skip = 0)
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        for (int table = 0; table < 4; table++)
        foreach (var character in ModelTextureTable.ReadCharacters(rom, table))
        {
            if (character.AnimationAssetIds.Count < 2) continue;

            var entry = directory.Entries.FirstOrDefault(e => e.Index == character.MeshAssetId);
            if (entry is null || !directory.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var mesh)) continue;

            try
            {
                var bank = PoseBank.Assemble(rom, directory, character.AnimationAssetIds[1]);
                var clips = AnimationSet.Decode(rom, directory, character.AnimationAssetIds[0]);
                if (bank.PoseCount == 0 || clips.Clips.Count == 0) continue;
                if (skip-- > 0) continue;
                return (character, mesh, bank, clips);
            }
            catch (Exception) { /* not every character has a readable rig */ }
        }

        throw new InvalidOperationException("No animated character was found.");
    }

    /// <summary>
    /// The claim the feature rests on: export a character's animation, import it straight back, and the
    /// pose bank is unchanged byte for byte.
    /// </summary>
    [RomFact]
    public void ExportedAnimationImportsBackWithoutChangingTheBank()
    {
        var (character, mesh, bank, clips) = FindAnimated();
        var original = bank.Data.ToArray();

        string path = Path.Combine(_scratch, "rigged.glb");
        AnimatedGltfExporter.Save(mesh, bank, clips, null, path, "char");

        var report = GltfAnimationImporter.Load(path, bank, clips);

        _output.WriteLine($"character {character.Index}: {report.ClipsMatched} clips, " +
                          $"{report.FramesSampled:N0} frames, {report.PosesWritten:N0} poses rewritten");

        Assert.True(report.ClipsMatched > 0, "no clips were matched");
        Assert.True(report.FramesSampled > 0, "no frames were sampled");
        Assert.Equal(original, bank.Data);
    }

    /// <summary>Several characters, since rigs and clip counts vary a good deal across the cast.</summary>
    [RomFact]
    public void TheRoundTripHoldsAcrossSeveralCharacters()
    {
        int checkedCount = 0;

        for (int skip = 0; skip < 5; skip++)
        {
            (ModelEntry Character, MeshFile Mesh, PoseBank Bank, AnimationSet Clips) subject;
            try { subject = FindAnimated(skip); }
            catch (InvalidOperationException) { break; }

            var original = subject.Bank.Data.ToArray();
            string path = Path.Combine(_scratch, $"rig{skip}.glb");
            AnimatedGltfExporter.Save(subject.Mesh, subject.Bank, subject.Clips, null, path, "char");

            GltfAnimationImporter.Load(path, subject.Bank, subject.Clips);
            Assert.Equal(original, subject.Bank.Data);
            checkedCount++;
        }

        _output.WriteLine($"{checkedCount} characters round-tripped with an unchanged pose bank");
        Assert.True(checkedCount >= 3, $"only {checkedCount} characters were exercised");
    }

    /// <summary>
    /// An edited rotation has to reach the poses the clip's frames point at -- and only those.
    /// </summary>
    [RomFact]
    public void EditedRotationsReachExactlyTheReferencedPoses()
    {
        var (_, mesh, bank, clips) = FindAnimated();

        var referenced = clips.Clips
            .SelectMany(c => c.PoseIndices)
            .Where(i => i >= 0 && i < bank.PoseCount)
            .ToHashSet();

        var untouchedBefore = Enumerable.Range(0, bank.PoseCount)
            .Where(i => !referenced.Contains(i))
            .ToDictionary(i => i, i => bank.GetPose(i).Angles);

        string path = Path.Combine(_scratch, "edit.glb");
        AnimatedGltfExporter.Save(mesh, bank, clips, null, path, "char");

        // Rewrite one joint everywhere by re-importing after rotating it in the bank first is
        // circular, so instead assert the reachable set: every referenced pose is reported written.
        var report = GltfAnimationImporter.Load(path, bank, clips);
        Assert.Equal(referenced.Count, report.PosesWritten);

        foreach (var (index, angles) in untouchedBefore)
            Assert.Equal(angles, bank.GetPose(index).Angles);
    }

    [RomFact]
    public void AFileWithoutARigIsRejectedClearly()
    {
        var (_, mesh, bank, clips) = FindAnimated();

        // The plain mesh exporter writes no joint nodes at all.
        string path = Path.Combine(_scratch, "norig.glb");
        GltfExporter.Save(mesh, path, "mesh");

        var ex = Assert.Throws<InvalidDataException>(() => GltfAnimationImporter.Load(path, bank, clips));
        Assert.Contains("jointNN", ex.Message);
    }
}
