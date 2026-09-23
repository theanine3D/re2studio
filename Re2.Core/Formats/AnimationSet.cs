using System;
using System.Collections.Generic;
using System.IO;
using Re2.Core.Assets;
using Re2.Core.Codecs;
using Re2.Core.Rom;

namespace Re2.Core.Formats;

/// <summary>One animation: an ordered list of pose indices, one per frame.</summary>
public sealed record AnimationClip(int Index, IReadOnlyList<int> PoseIndices)
{
    public int FrameCount => PoseIndices.Count;
}

/// <summary>A character's animation clips.</summary>
public sealed class AnimationSet
{
    /// <summary>Pose index occupies the low 12 bits of a frame word.</summary>
    public const uint PoseIndexMask = 0x0FFF;

    public IReadOnlyList<AnimationClip> Clips { get; }

    /// <summary>Raw frame words, keyed by clip then frame, for callers that want the flag bits.</summary>
    public IReadOnlyList<IReadOnlyList<uint>> RawFrames { get; }

    private AnimationSet(List<AnimationClip> clips, List<IReadOnlyList<uint>> raw)
    {
        Clips = clips;
        RawFrames = raw;
    }

    public static AnimationSet Decode(RomFile rom, AssetDirectory directory, int assetId)
    {
        AssetEntry? entry = null;
        foreach (var e in directory.Entries) if (e.Index == assetId) { entry = e; break; }

        if (entry is null || !directory.TryGetData(rom, entry, out var data))
            throw new InvalidDataException($"Animation asset {assetId} is missing or undecodable.");

        if (!BitStreamCodec.TryDecode(data, out var decoded))
            throw new InvalidDataException($"Animation asset {assetId} did not decode.");

        var clips = new List<AnimationClip>();
        var raw = new List<IReadOnlyList<uint>>();

        foreach (var (offset, count) in decoded.Table)
        {
            if (count <= 0 || offset <= 0) continue;

            int first = offset / 4;                    // the table stores byte offsets
            if (first + count > decoded.Data.Length) continue;

            var poses = new List<int>(count);
            var words = new List<uint>(count);
            for (int f = 0; f < count; f++)
            {
                uint word = decoded.Data[first + f];
                words.Add(word);
                poses.Add((int)(word & PoseIndexMask));
            }

            raw.Add(words);
            clips.Add(new AnimationClip(clips.Count, poses));
        }

        return new AnimationSet(clips, raw);
    }
}
