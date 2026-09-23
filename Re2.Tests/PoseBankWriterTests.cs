using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Export;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

public sealed class PoseBankWriterTests
{
    private readonly ITestOutputHelper _output;
    public PoseBankWriterTests(ITestOutputHelper output) => _output = output;

    private static List<(int IndexAssetId, PoseBank Bank)> LoadBanks(int take)
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var banks = new List<(int, PoseBank)>();
        var seen = new HashSet<int>();

        for (int table = 0; table < 4 && banks.Count < take; table++)
        foreach (var character in ModelTextureTable.ReadCharacters(rom, table))
        {
            if (banks.Count >= take) break;
            if (character.AnimationAssetIds.Count < 2) continue;

            int id = character.AnimationAssetIds[1];
            if (!seen.Add(id)) continue;

            try
            {
                var bank = PoseBank.Assemble(rom, directory, id);
                if (bank.PoseCount > 0 && bank.PartCount > 0) banks.Add((id, bank));
            }
            catch (Exception) { /* not every slot is a pose bank */ }
        }

        return banks;
    }

    /// <summary>
    /// Reading every pose and writing it straight back must leave the bank untouched.
    /// </summary>
    [RomFact]
    public void EveryPoseReWritesToTheSameBytes()
    {
        var banks = LoadBanks(12);
        Assert.NotEmpty(banks);

        long poses = 0;
        foreach (var (id, bank) in banks)
        {
            var original = bank.Data.ToArray();

            for (int p = 0; p < bank.PoseCount; p++)
            {
                var pose = bank.GetPose(p);
                PoseBankWriter.WritePose(bank, p, pose.RootTranslation, pose.Angles);
                poses++;
            }

            Assert.Equal(original, bank.Data);
        }

        _output.WriteLine($"{banks.Count} banks, {poses:N0} poses re-written byte-identically");
        Assert.True(poses > 500, $"only {poses} poses were exercised");
    }

    /// <summary>An edited angle must read back exactly, and must not disturb its neighbours.</summary>
    [RomFact]
    public void EditingOneAngleChangesOnlyThatAngle()
    {
        var (_, bank) = LoadBanks(1)[0];
        int poseIndex = Math.Min(3, bank.PoseCount - 1);

        var before = bank.GetPose(poseIndex);
        var neighbourBefore = bank.PoseCount > poseIndex + 1 ? bank.GetPose(poseIndex + 1) : null;

        var angles = before.Angles.ToArray();
        int joint = Math.Min(2, bank.PartCount - 1);
        angles[joint] = (1234, 2345, 3456);

        PoseBankWriter.WritePose(bank, poseIndex, before.RootTranslation, angles);

        var after = bank.GetPose(poseIndex);
        Assert.Equal((1234, 2345, 3456), after.Angles[joint]);
        Assert.Equal(before.RootTranslation, after.RootTranslation);

        for (int j = 0; j < bank.PartCount; j++)
            if (j != joint)
                Assert.Equal(before.Angles[j], after.Angles[j]);

        if (neighbourBefore is not null)
            Assert.Equal(neighbourBefore.Angles, bank.GetPose(poseIndex + 1).Angles);
    }

    [RomFact]
    public void RootTranslationRoundTrips()
    {
        var (_, bank) = LoadBanks(1)[0];
        var pose = bank.GetPose(0);

        PoseBankWriter.WritePose(bank, 0, (-1234, 567, -890), pose.Angles);

        var after = bank.GetPose(0);
        Assert.Equal(((short)-1234, (short)567, (short)-890), after.RootTranslation);
        Assert.Equal(pose.Angles, after.Angles);
    }

    [RomFact]
    public void AnglesOutsideTwelveBitsAreRejected()
    {
        var (_, bank) = LoadBanks(1)[0];
        var pose = bank.GetPose(0);
        var angles = pose.Angles.ToArray();
        angles[0] = (4096, 0, 0);

        var ex = Assert.Throws<System.IO.InvalidDataException>(
            () => PoseBankWriter.WritePose(bank, 0, pose.RootTranslation, angles));
        Assert.Contains("0..4095", ex.Message);
    }

    /// <summary>
    /// The layout walk has to agree with the assembler, or an edited bank would be cut back into the
    /// wrong assets. Splitting an untouched bank must reproduce each chunk asset byte for byte.
    /// </summary>
    [RomFact]
    public void SplittingAnUntouchedBankReproducesItsAssets()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var banks = LoadBanks(10);
        int compared = 0;

        foreach (var (indexAssetId, bank) in banks)
        {
            var chunks = PoseBankWriter.Layout(rom, directory, indexAssetId);
            Assert.NotEmpty(chunks);

            var split = PoseBankWriter.SplitToAssets(bank.Data, chunks, out var warnings);
            Assert.Empty(warnings);

            foreach (var (assetId, bytes) in split)
            {
                var entry = directory.Entries.First(e => e.Index == assetId);
                Assert.True(directory.TryGetData(rom, entry, out var original));
                Assert.Equal(original, bytes);
                compared++;
            }
        }

        _output.WriteLine($"{banks.Count} banks split back into {compared} chunk assets, all byte-identical");
        Assert.True(compared > 10, $"only {compared} chunks were compared");
    }

    [Fact]
    public void RadiansConvertToTwelveBitUnitsAndWrap()
    {
        Assert.Equal(0, PoseBankWriter.FromRadians(0));
        Assert.Equal(1024, PoseBankWriter.FromRadians(MathF.PI / 2));
        Assert.Equal(2048, PoseBankWriter.FromRadians(MathF.PI));
        Assert.Equal(0, PoseBankWriter.FromRadians(2 * MathF.PI));
        Assert.Equal(3072, PoseBankWriter.FromRadians(-MathF.PI / 2));
        Assert.InRange(PoseBankWriter.FromRadians(100f), 0, PoseBankWriter.MaxAngle);
    }

    /// <summary>
    /// The rotation convention must survive a trip through a quaternion, because that is the only form
    /// glTF can carry.
    /// </summary>
    [RomFact]
    public void EveryPoseAngleSurvivesTheQuaternionRoundTrip()
    {
        var banks = LoadBanks(10);
        long joints = 0;
        double worst = 0;

        foreach (var (_, bank) in banks)
        for (int p = 0; p < bank.PoseCount; p++)
        {
            var pose = bank.GetPose(p);
            foreach (var (ax, ay, az) in pose.Angles)
            {
                var original = AnimatedGltfExporter.EulerToQuaternion(ax, ay, az);
                var (rx, ry, rz) = AnimatedGltfExporter.QuaternionToEuler(original);
                var again = AnimatedGltfExporter.EulerToQuaternion(rx, ry, rz);

                // q and -q are the same rotation, so compare on the absolute dot product.
                double dot = Math.Abs(System.Numerics.Quaternion.Dot(original, again));
                worst = Math.Max(worst, 1 - dot);
                joints++;
            }
        }

        _output.WriteLine($"{joints:N0} joint rotations round-tripped; worst deviation {worst:E2}");
        Assert.True(joints > 50_000, $"only {joints} rotations were exercised");
        Assert.True(worst < 1e-4, $"a rotation moved by {worst:E2} through the quaternion round trip");
    }
}
