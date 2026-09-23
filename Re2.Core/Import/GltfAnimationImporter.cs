using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Re2.Core.Export;
using Re2.Core.Formats;
using SharpGLTF.Schema2;

namespace Re2.Core.Import;

/// <summary>What an animation import changed, and what it could not.</summary>
public sealed record AnimationImportReport(
    int ClipsMatched, int FramesSampled, int PosesWritten, int PosesReused, int JointsMissing)
{
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Reads joint rotations out of a glTF's animation tracks and writes them into a pose bank -- the
/// inverse of the animation half of <see cref="AnimatedGltfExporter"/>.
/// </summary>
public static class GltfAnimationImporter
{
    /// <summary>The exporter's <c>jointNN</c> node naming, which carries the part index.</summary>
    private static readonly Regex JointName = new(@"joint[_\-]?(\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The exporter's <c>clipNN</c> track naming.</summary>
    private static readonly Regex ClipName = new(@"clip[_\-]?(\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Samples every animation in <paramref name="path"/> and writes the result into
    /// <paramref name="bank"/> in place.
    /// </summary>
    public static AnimationImportReport Load(string path, PoseBank bank, AnimationSet clips,
                                             float frameRate = AnimatedGltfExporter.FrameRate)
    {
        var root = ModelRoot.Load(path);
        var warnings = new List<string>();

        // Map the rig's nodes back to part indices.
        var nodeOfPart = new Dictionary<int, Node>();
        foreach (var node in root.LogicalNodes)
        {
            var match = JointName.Match(node.Name ?? "");
            if (!match.Success) continue;
            int part = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (part < bank.PartCount) nodeOfPart[part] = node;
        }

        if (nodeOfPart.Count == 0)
            throw new InvalidDataException(
                "No jointNN nodes were found. The file needs the skeleton this ROM's exporter writes; " +
                "a model exported without its rig carries no animation this can read.");

        int missing = bank.PartCount - nodeOfPart.Count;
        if (missing > 0)
            warnings.Add($"{missing} of the rig's {bank.PartCount} joints have no node in the file; " +
                         "those joints keep their existing rotations.");

        // Remember what each pose held so a genuine collision can be told from a repeat write of
        // the same value, which is harmless and very common.
        var written = new Dictionary<int, (int X, int Y, int Z)[]>();
        int clipsMatched = 0, frames = 0, reused = 0;

        foreach (var animation in root.LogicalAnimations)
        {
            var match = ClipName.Match(animation.Name ?? "");
            if (!match.Success)
            {
                warnings.Add($"animation '{animation.Name}' has no clipNN name and was skipped.");
                continue;
            }

            int clipIndex = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var clip = clips.Clips.FirstOrDefault(c => c.Index == clipIndex);
            if (clip is null)
            {
                warnings.Add($"animation '{animation.Name}' has no matching clip in the ROM and was skipped.");
                continue;
            }

            // One sampler per joint, built once rather than per frame.
            var samplers = new Dictionary<int, SharpGLTF.Animations.ICurveSampler<Quaternion>>();
            foreach (var (part, node) in nodeOfPart)
            {
                var channel = animation.FindRotationChannel(node);
                var sampler = channel?.GetRotationSampler()?.CreateCurveSampler();
                if (sampler is not null) samplers[part] = sampler;
            }

            if (samplers.Count == 0)
            {
                warnings.Add($"'{animation.Name}' has no rotation tracks and was skipped.");
                continue;
            }

            clipsMatched++;

            for (int frame = 0; frame < clip.FrameCount; frame++)
            {
                int poseIndex = clip.PoseIndices[frame];
                if (poseIndex < 0 || poseIndex >= bank.PoseCount) continue;

                var pose = bank.GetPose(poseIndex);
                var angles = pose.Angles.ToArray();

                float time = frame / frameRate;
                foreach (var (part, sampler) in samplers)
                    // Undo the axis flip the exporter applied before reading the angles back.
                    angles[part] = AnimatedGltfExporter.QuaternionToEuler(
                        Export.GltfExporter.ToGltfAxes(sampler.GetPoint(time)));

                if (written.TryGetValue(poseIndex, out var already))
                {
                    if (!already.SequenceEqual(angles))
                    {
                        reused++;
                        if (reused <= 5)
                            warnings.Add($"pose {poseIndex} is shared and received conflicting rotations " +
                                         $"(clip {clipIndex}, frame {frame}); the last write wins.");
                    }
                }

                PoseBankWriter.WritePose(bank, poseIndex, pose.RootTranslation, angles);
                written[poseIndex] = angles;
                frames++;
            }
        }

        if (reused > 5)
            warnings.Add($"{reused - 5} further shared-pose conflicts were not listed.");

        if (clipsMatched == 0)
            throw new InvalidDataException("None of the file's animations matched a clip in the ROM.");

        return new AnimationImportReport(clipsMatched, frames, written.Count, reused, missing)
        {
            Warnings = warnings
        };
    }
}
