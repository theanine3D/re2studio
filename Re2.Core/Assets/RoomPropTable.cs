using System;
using System.Collections.Generic;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>One scenery model a room places: its mesh and the texture set it draws from.</summary>
public sealed record RoomProp(int Stage, int Room, int Slot, int MeshAssetId, int PairIndex);

/// <summary>
/// The per-room lists of scenery models -- the crates, statues, machinery and other props a room puts
/// on screen.
/// </summary>
public static class RoomPropTable
{
    /// <summary>Holds the two root pointers the loader chooses between.</summary>
    public const uint RootTableAddress = 0x8012BA84;

    public const int RootVariants = 2;

    /// <summary>The props every room loads, whichever room it is.</summary>
    public const uint GlobalListAddress = 0x8012BA8C;

    /// <summary>Entries at or above this are empty slots.</summary>
    private const ushort Empty = 0xFFF0;

    private const int MaxCount = 64;
    private const int MaxStages = 64;
    private const int MaxRooms = 256;

    public static List<RoomProp> Read(RomFile rom)
    {
        var directory = AssetDirectory.Read(rom);
        var models = new HashSet<int>();

        foreach (var entry in directory.Entries)
            if (directory.TryGetData(rom, entry, out var data) && Formats.MeshFile.TryParse(data, out _))
                models.Add(entry.Index);

        return Read(ModelTextureTable.LoadMainOverlay(rom), models);
    }

    /// <summary>Every placement, keeping repeats.</summary>
    public static List<RoomProp> ReadAll(ModelTextureTable.Overlay overlay, IReadOnlySet<int> models)
        => Read(overlay, models, keepDuplicates: true);

    public static List<RoomProp> Read(ModelTextureTable.Overlay overlay, IReadOnlySet<int> models)
        => Read(overlay, models, keepDuplicates: false);

    private static List<RoomProp> Read(ModelTextureTable.Overlay overlay, IReadOnlySet<int> models,
                                       bool keepDuplicates)
    {
        var props = new List<RoomProp>();
        var seen = new HashSet<(int Mesh, int Pair)>();

        // Reads one list, or reports that this is not one.
        bool ReadList(uint at, int stage, int room)
        {
            if (!overlay.Contains(at, 2)) return false;

            int count = overlay.U16(at) & 0xFF;          // the count is the low byte of the first word
            if (count is <= 0 or > MaxCount) return false;
            if (!overlay.Contains(at, 2 + count * 4)) return false;

            var found = new List<RoomProp>(count);

            for (int i = 0; i < count; i++)
            {
                int mesh = overlay.U16(at + 2 + (uint)i * 4);
                int pairIndex = overlay.U16(at + 4 + (uint)i * 4);
                if (mesh >= Empty) continue;

                if (!models.Contains(mesh)) return false;
                if (ModelTextureTable.ReadPair(overlay, pairIndex) is null) return false;

                found.Add(new RoomProp(stage, room, i, mesh, pairIndex));
            }

            foreach (var prop in found)
                if (keepDuplicates || seen.Add((prop.MeshAssetId, prop.PairIndex))) props.Add(prop);

            return true;
        }

        ReadList(overlay.At(GlobalListAddress), -1, -1);

        for (int variant = 0; variant < RootVariants; variant++)
        {
            uint rootAt = overlay.At(RootTableAddress) + (uint)variant * 4;
            if (!overlay.Contains(rootAt, 4)) continue;

            uint root = U32(overlay, rootAt);
            if (!overlay.Contains(root, 4)) continue;

            for (int stage = 0; stage < MaxStages; stage++)
            {
                uint stageAt = root + (uint)stage * 4;
                if (!overlay.Contains(stageAt, 4)) break;

                uint stageArray = U32(overlay, stageAt);
                if (!overlay.Contains(stageArray, 4)) continue;

                // A slot that does not hold a list is skipped rather than treated as the end of the
                // array: rooms with no props of their own sit in the middle of these tables, and
                // stopping at the first one loses everything after it.
                for (int room = 0; room < MaxRooms; room++)
                {
                    uint roomAt = stageArray + (uint)room * 4;
                    if (!overlay.Contains(roomAt, 4)) break;

                    uint list = U32(overlay, roomAt);
                    if (!overlay.Contains(list, 2)) continue;

                    ReadList(list, stage, room);
                }
            }
        }

        return props;
    }

    private static uint U32(ModelTextureTable.Overlay overlay, uint at)
        => ((uint)overlay.U16(at) << 16) | overlay.U16(at + 2);
}
