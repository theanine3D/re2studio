using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Re2.Core.Assets;
using Re2.Core.Rom;

namespace Re2.Core.Formats;

/// <summary>One joint of the rest pose: its offset from its parent, plus its children.</summary>
public sealed record RestJoint(int Index, short X, short Y, short Z, IReadOnlyList<int> Children);

/// <summary>
/// One frame of animation: a root translation plus a rotation per part, as raw 12-bit units.
/// </summary>
public sealed record Pose(int Index, (short X, short Y, short Z) RootTranslation, (int X, int Y, int Z)[] Angles);

/// <summary>A character's skeleton and pose bank.</summary>
public sealed class PoseBank
{
    public const int RestOffsetsOffset = 8;

    /// <summary>Root translation ahead of the packed angles in each pose.</summary>
    public const int PoseRootSize = 12;

    /// <summary>Bits per Euler angle; three per part.</summary>
    public const int AngleBits = 12;

    /// <summary>A full turn in raw angle units.</summary>
    public const int AngleUnitsPerTurn = 1 << AngleBits;

    /// <summary>Converts a raw 12-bit angle to radians. Values wrap, so sign does not matter.</summary>
    public static float AngleToRadians(int raw) => raw * (2f * MathF.PI / AngleUnitsPerTurn);

    public byte[] Data { get; }
    public int PoseDataOffset { get; }
    public int HierarchyOffset { get; }
    public int BytesPerPose { get; }
    public int PartCount { get; }
    public IReadOnlyList<RestJoint> Joints { get; }

    public int PoseCount => BytesPerPose > 0 ? (Data.Length - PoseDataOffset) / BytesPerPose : 0;

    /// <summary>Root joint index. Always 0 in retail data, but derived rather than assumed.</summary>
    public int RootIndex { get; }

    private PoseBank(byte[] data, int poseDataOffset, int hierarchyOffset, int bytesPerPose,
        int partCount, List<RestJoint> joints, int rootIndex)
    {
        Data = data;
        PoseDataOffset = poseDataOffset;
        HierarchyOffset = hierarchyOffset;
        BytesPerPose = bytesPerPose;
        PartCount = partCount;
        Joints = joints;
        RootIndex = rootIndex;
    }

    private static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.Slice(o, 2));
    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.Slice(o, 4));

    /// <summary>
    /// Rebuilds the bank from its index asset, following the game's own assembly order.
    /// </summary>
    public static PoseBank Assemble(RomFile rom, AssetDirectory directory, int indexAssetId)
    {
        var byId = new Dictionary<int, AssetEntry>();
        foreach (var e in directory.Entries) byId[e.Index] = e;

        if (!byId.TryGetValue(indexAssetId, out var indexEntry) || !directory.TryGetData(rom, indexEntry, out var index))
            throw new InvalidDataException($"Pose index asset {indexAssetId} is missing or undecodable.");
        if (index.Length < 10) throw new InvalidDataException("Pose index asset is too short.");

        var buffer = new List<byte>();

        // The two header words arrive with their halfword pairs swapped.
        for (int k = 0; k < 2; k++)
        {
            ushort a = U16(index, k * 4);
            ushort b = U16(index, k * 4 + 2);
            buffer.Add((byte)(b >> 8)); buffer.Add((byte)b);
            buffer.Add((byte)(a >> 8)); buffer.Add((byte)a);
        }

        int bytesPerPose = U16(buffer.ToArray(), 4);
        int entryCount = U16(index, 8);

        for (int k = 0; k < entryCount; k++)
        {
            int at = 10 + k * 2;
            if (at + 2 > index.Length) break;

            ushort entry = U16(index, at);
            int assetId = entry & 0x0FFF;
            int repeat = (entry >> 12) + 1;

            if (assetId == 0)
            {
                for (int r = 0; r < repeat * bytesPerPose; r++) buffer.Add(0);
                continue;
            }

            if (!byId.TryGetValue(assetId, out var part) || !directory.TryGetData(rom, part, out var chunk))
                throw new InvalidDataException($"Pose bank references asset {assetId}, which will not decode.");

            for (int r = 0; r < repeat; r++) buffer.AddRange(chunk);
        }

        return Parse(buffer.ToArray());
    }

    public static PoseBank Parse(byte[] data)
    {
        if (data.Length < RestOffsetsOffset) throw new InvalidDataException("Pose bank is too short.");

        int poseDataOffset = U16(data, 0);
        int hierarchyOffset = U16(data, 2) & 0xFFFC;
        int bytesPerPose = U16(data, 4);
        int partCount = U16(data, 6);

        if (partCount is <= 0 or > 128) throw new InvalidDataException($"Implausible part count {partCount}.");
        if (hierarchyOffset + partCount * 4 > data.Length) throw new InvalidDataException("Hierarchy table is out of range.");

        // Halfword pairs are stored swapped, hence the ^1 on the index.
        short Rest(int halfwordIndex)
            => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(RestOffsetsOffset + (halfwordIndex ^ 1) * 2, 2));

        var joints = new List<RestJoint>(partCount);
        var hasParent = new bool[partCount];

        for (int p = 0; p < partCount; p++)
        {
            uint word = U32(data, hierarchyOffset + p * 4);
            int childCount = (int)(word & 0xFFFF);
            int childOffset = (int)(word >> 16);

            var children = new List<int>(childCount);
            for (int c = 0; c < childCount; c++)
            {
                int at = (hierarchyOffset + childOffset + c) ^ 3;   // bytes swapped within each word
                if (at < 0 || at >= data.Length) break;
                int child = data[at];
                if (child < partCount) { children.Add(child); hasParent[child] = true; }
            }

            joints.Add(new RestJoint(p, Rest(p * 3), Rest(p * 3 + 1), Rest(p * 3 + 2), children));
        }

        int root = 0;
        for (int p = 0; p < partCount; p++) if (!hasParent[p]) { root = p; break; }

        return new PoseBank(data, poseDataOffset, hierarchyOffset, bytesPerPose, partCount, joints, root);
    }

    /// <summary>Decodes one pose.</summary>
    public Pose GetPose(int index)
    {
        if (index < 0 || index >= PoseCount) throw new ArgumentOutOfRangeException(nameof(index));

        int at = PoseDataOffset + index * BytesPerPose;

        short Root(int halfword) => BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(at + (halfword ^ 1) * 2, 2));
        var root = (Root(0), Root(1), Root(2));

        int start = at + PoseRootSize;
        int available = BytesPerPose - PoseRootSize;

        ulong window = 0;
        int windowBits = 0;
        int cursor = 0;

        int NextAngle()
        {
            while (windowBits < AngleBits)
            {
                uint word = 0;
                if (cursor + 4 <= available) word = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(start + cursor, 4));
                cursor += 4;
                window |= (ulong)word << windowBits;
                windowBits += 32;
            }

            int value = (int)(window & (AngleUnitsPerTurn - 1));
            window >>= AngleBits;
            windowBits -= AngleBits;
            return value;
        }

        var angles = new (int X, int Y, int Z)[PartCount];
        for (int p = 0; p < PartCount; p++) angles[p] = (NextAngle(), NextAngle(), NextAngle());

        return new Pose(index, root, angles);
    }

    /// <summary>
    /// Accumulates a supplied set of per-joint offsets down this skeleton's hierarchy.
    /// </summary>
    public (int X, int Y, int Z)[] WorldPositionsFrom(IReadOnlyList<(int X, int Y, int Z)> locals)
    {
        var world = new (int X, int Y, int Z)[PartCount];
        var visited = new bool[PartCount];
        var stack = new Stack<(int Joint, (int X, int Y, int Z) Parent)>();
        stack.Push((RootIndex, (0, 0, 0)));

        while (stack.Count > 0)
        {
            var (index, parent) = stack.Pop();
            if (index < 0 || index >= PartCount || visited[index]) continue;
            visited[index] = true;

            var local = index < locals.Count ? locals[index] : (Joints[index].X, Joints[index].Y, Joints[index].Z);
            var position = (parent.X + local.X, parent.Y + local.Y, parent.Z + local.Z);
            world[index] = position;

            foreach (int child in Joints[index].Children) stack.Push((child, position));
        }

        return world;
    }

    public (int X, int Y, int Z)[] RestWorldPositions()
    {
        var world = new (int X, int Y, int Z)[PartCount];
        var visited = new bool[PartCount];
        var stack = new Stack<(int Joint, (int X, int Y, int Z) Parent)>();
        stack.Push((RootIndex, (0, 0, 0)));

        while (stack.Count > 0)
        {
            var (index, parent) = stack.Pop();
            if (index < 0 || index >= PartCount || visited[index]) continue;
            visited[index] = true;

            var joint = Joints[index];
            var position = (parent.X + joint.X, parent.Y + joint.Y, parent.Z + joint.Z);
            world[index] = position;

            foreach (int child in joint.Children) stack.Push((child, position));
        }

        return world;
    }
}
