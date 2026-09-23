using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public class PoseBankTests
{
    /// <summary>Character 0's pose bank index asset (slot [2] of its asset list).</summary>
    private const int Character0PoseIndex = 5594;

    private static PoseBank Bank(int indexAssetId = Character0PoseIndex)
    {
        var rom = TestRom.Rom;
        return PoseBank.Assemble(rom, AssetDirectory.Read(rom), indexAssetId);
    }

    [RomFact]
    public void AssemblesCharacterZerosSkeleton()
    {
        var bank = Bank();

        Assert.Equal(15, bank.PartCount);
        Assert.Equal(80, bank.BytesPerPose);
        Assert.Equal(176, bank.PoseDataOffset);
        Assert.Equal(100, bank.HierarchyOffset);
        Assert.Equal(234, bank.PoseCount);
        Assert.Equal(0, bank.RootIndex);
    }

    /// <summary>
    /// A pose is a 12-byte root translation followed by three 12-bit angles per part, so the stride
    /// must be exactly that rounded up. Getting the part count or the packing wrong breaks this.
    /// </summary>
    [RomFact]
    public void PoseStrideMatchesTheAnglePacking()
    {
        var bank = Bank();
        int bits = bank.PartCount * 3 * PoseBank.AngleBits;
        int expected = PoseBank.PoseRootSize + (bits + 7) / 8;

        Assert.Equal(expected, bank.BytesPerPose);
    }

    /// <summary>The pose data must divide evenly; a remainder would mean a wrong stride or offset.</summary>
    [RomFact]
    public void PoseDataDividesEvenly()
    {
        var bank = Bank();
        Assert.Equal(0, (bank.Data.Length - bank.PoseDataOffset) % bank.BytesPerPose);
    }

    /// <summary>Exactly one root, every joint reachable from it, and no cycles.</summary>
    [RomFact]
    public void HierarchyIsAWellFormedTree()
    {
        var bank = Bank();

        var parents = new int[bank.PartCount];
        Array.Fill(parents, -1);

        foreach (var joint in bank.Joints)
            foreach (int child in joint.Children)
            {
                Assert.InRange(child, 0, bank.PartCount - 1);
                Assert.Equal(-1, parents[child]);        // no joint may have two parents
                parents[child] = joint.Index;
            }

        Assert.Single(Enumerable.Range(0, bank.PartCount).Where(p => parents[p] == -1));

        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(bank.RootIndex);
        while (stack.Count > 0)
        {
            int at = stack.Pop();
            Assert.True(seen.Add(at), "hierarchy contains a cycle");
            foreach (int child in bank.Joints[at].Children) stack.Push(child);
        }

        Assert.Equal(bank.PartCount, seen.Count);
    }

    /// <summary>The rig is a humanoid, so limb pairs must mirror in Z.</summary>
    [RomFact]
    public void LimbPairsMirrorInZ()
    {
        var bank = Bank();

        foreach (var (left, right) in new[] { (2, 5), (9, 12) })
        {
            var a = bank.Joints[left];
            var b = bank.Joints[right];
            Assert.True(Math.Sign(a.Z) == -Math.Sign(b.Z), $"joints {left}/{right} are not on opposite sides");
            Assert.True(Math.Abs(Math.Abs(a.Z) - Math.Abs(b.Z)) <= 4, $"joints {left}/{right} are not symmetric");
        }
    }

    /// <summary>
    /// The whole point of the rest pose: parts must end up spread through space, not stacked at the
    /// origin. A humanoid should be considerably taller than it is wide.
    /// </summary>
    [RomFact]
    public void RestPoseSpreadsPartsIntoAFigure()
    {
        var world = Bank().RestWorldPositions();

        int spanX = world.Max(p => p.X) - world.Min(p => p.X);
        int spanY = world.Max(p => p.Y) - world.Min(p => p.Y);
        int spanZ = world.Max(p => p.Z) - world.Min(p => p.Z);

        Assert.True(spanY > 1500, $"figure is only {spanY} units tall");
        Assert.True(spanY > spanX && spanY > spanZ, "figure is not taller than it is wide");
        Assert.True(spanZ > 500, $"limbs only span {spanZ} units across");
    }

    [RomFact]
    public void EveryCharacterWithAPoseBankAssembles()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);
        var overlay = ModelTextureTable.LoadMainOverlay(rom);

        int ok = 0;
        foreach (var character in ModelTextureTable.ReadCharacters(overlay))
        {
            if (character.AnimationAssetIds.Count < 2) continue;
            try
            {
                var bank = PoseBank.Assemble(rom, dir, character.AnimationAssetIds[1]);
                Assert.InRange(bank.PartCount, 1, 64);
                Assert.True(bank.PoseCount > 0);
                ok++;
            }
            catch (Exception)
            {
                // Not every entity is a rigged character.
            }
        }

        Assert.True(ok >= 10, $"only {ok} characters produced a usable pose bank");
    }
}
