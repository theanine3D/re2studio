using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public class AnimationTests
{
    private const int Character0Clips = 5593;
    private const int Character0PoseIndex = 5594;

    private static AnimationSet Clips()
    {
        var rom = TestRom.Rom;
        return AnimationSet.Decode(rom, AssetDirectory.Read(rom), Character0Clips);
    }

    private static PoseBank Bank()
    {
        var rom = TestRom.Rom;
        return PoseBank.Assemble(rom, AssetDirectory.Read(rom), Character0PoseIndex);
    }

    [RomFact]
    public void DecodesCharacterZerosClips()
    {
        var clips = Clips();
        Assert.Equal(new[] { 65, 44, 54, 54, 60, 41, 55, 38 }, clips.Clips.Select(c => c.FrameCount));
    }

    /// <summary>Every frame must name a pose the bank actually holds.</summary>
    [RomFact]
    public void EveryFrameReferencesAValidPose()
    {
        var bank = Bank();
        foreach (var clip in Clips().Clips)
            foreach (int pose in clip.PoseIndices)
                Assert.InRange(pose, 0, bank.PoseCount - 1);
    }

    [RomFact]
    public void DecodesOneAnglePerAxisPerPart()
    {
        var bank = Bank();
        for (int i = 0; i < Math.Min(bank.PoseCount, 40); i++)
        {
            var pose = bank.GetPose(i);
            Assert.Equal(bank.PartCount, pose.Angles.Length);
            foreach (var (x, y, z) in pose.Angles)
            {
                Assert.InRange(x, 0, PoseBank.AngleUnitsPerTurn - 1);
                Assert.InRange(y, 0, PoseBank.AngleUnitsPerTurn - 1);
                Assert.InRange(z, 0, PoseBank.AngleUnitsPerTurn - 1);
            }
        }
    }

    /// <summary>
    /// The strongest signal that the bit unpacking is right: real joint rotations cluster near zero (a
    /// limb rarely bends far), so most angles should sit close to 0 or close to a full turn.
    /// </summary>
    [RomFact]
    public void AnglesClusterNearZeroAsRealJointsDo()
    {
        var bank = Bank();
        int near = 0, total = 0;
        int quarter = PoseBank.AngleUnitsPerTurn / 4;

        for (int i = 0; i < Math.Min(bank.PoseCount, 60); i++)
            foreach (var (x, y, z) in bank.GetPose(i).Angles)
                foreach (int raw in new[] { x, y, z })
                {
                    total++;
                    int signed = raw >= PoseBank.AngleUnitsPerTurn / 2 ? raw - PoseBank.AngleUnitsPerTurn : raw;
                    if (Math.Abs(signed) < quarter) near++;
                }

        Assert.True(near > total * 3 / 4, $"only {near} of {total} angles are within a quarter turn of rest");
    }

    /// <summary>Successive frames of a clip must not jump wildly, or the motion would not be smooth.</summary>
    [RomFact]
    public void MotionIsContinuousAcrossFrames()
    {
        var bank = Bank();
        var clip = Clips().Clips[0];

        int jumps = 0, comparisons = 0;
        int half = PoseBank.AngleUnitsPerTurn / 2;

        for (int f = 1; f < clip.FrameCount; f++)
        {
            var a = bank.GetPose(clip.PoseIndices[f - 1]).Angles;
            var b = bank.GetPose(clip.PoseIndices[f]).Angles;

            for (int p = 0; p < a.Length; p++)
            {
                int delta = Math.Abs(a[p].X - b[p].X);
                if (delta > half) delta = PoseBank.AngleUnitsPerTurn - delta;   // angles wrap
                comparisons++;
                if (delta > PoseBank.AngleUnitsPerTurn / 8) jumps++;
            }
        }

        Assert.True(jumps < comparisons / 20, $"{jumps} of {comparisons} frame-to-frame steps exceed 45 degrees");
    }

    /// <summary>A clip has to actually move.</summary>
    [RomFact]
    public void ClipsActuallyAnimate()
    {
        var bank = Bank();
        var clips = Clips();

        int moving = 0;
        foreach (var clip in clips.Clips)
        {
            if (clip.FrameCount < 2) continue;

            var distinct = clip.PoseIndices.Distinct().Count();
            Assert.True(distinct > 1,
                $"clip {clip.Index} names the same pose on all {clip.FrameCount} frames");

            // And the poses it names must genuinely differ, not merely be different indices.
            bool changed = false;
            for (int f = 1; f < clip.FrameCount && !changed; f++)
            {
                var a = bank.GetPose(clip.PoseIndices[f - 1]).Angles;
                var b = bank.GetPose(clip.PoseIndices[f]).Angles;
                for (int j = 0; j < a.Length; j++)
                    if (a[j] != b[j]) { changed = true; break; }
            }

            Assert.True(changed, $"clip {clip.Index} holds one fixed pose across every frame");
            moving++;
        }

        Assert.True(moving >= 5, $"only {moving} clips had enough frames to check");
    }

    /// <summary>Frame words carry the pose index in their low 12 bits and flags above.</summary>
    [RomFact]
    public void FrameWordsUseTheWholePoseBank()
    {
        var bank = Bank();
        var referenced = Clips().Clips.SelectMany(c => c.PoseIndices).ToList();

        Assert.NotEmpty(referenced);

        int zeros = referenced.Count(p => p == 0);
        Assert.True(zeros < referenced.Count / 4,
            $"{zeros} of {referenced.Count} frames name pose 0; the low half of the frame words is empty");

        int distinct = referenced.Distinct().Count();
        Assert.True(distinct > bank.PoseCount / 8,
            $"only {distinct} distinct poses are referenced out of {bank.PoseCount} in the bank");
    }

    [RomFact]
    public void EulerConversionRoundTripsIdentity()
    {
        var q = Re2.Core.Export.AnimatedGltfExporter.EulerToQuaternion(0, 0, 0);
        Assert.Equal(System.Numerics.Quaternion.Identity, q);
    }
}
