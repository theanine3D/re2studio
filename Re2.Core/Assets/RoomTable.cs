using System;
using System.Collections.Generic;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>One room, and the pre-rendered backgrounds its camera angles draw.</summary>
public sealed record RoomEntry(int Stage, int Room, int FlatIndex, IReadOnlyList<int> BackgroundAssetIds)
{
    public int ViewCount => BackgroundAssetIds.Count;
    public override string ToString() => $"stage {Stage} room {Room} ({ViewCount} views)";
}

/// <summary>The game's rooms, and which background each of their camera angles shows.</summary>
public static class RoomTable
{
    /// <summary>Holds one pointer per stage.</summary>
    public const uint RootAddress = 0x8011C3B0;

    /// <summary>Where the background id data stops and the per-stage room pointers begin.</summary>
    public const uint DataEnd = 0x8011C188;

    /// <summary>Rooms in each stage. Derived from the loader's tables; see the class remarks.</summary>
    public static readonly int[] RoomsPerStage = { 30, 28, 14, 18, 10, 24, 6 };

    public static int StageCount => RoomsPerStage.Length;
    public static int TotalRooms
    {
        get
        {
            int total = 0;
            foreach (int n in RoomsPerStage) total += n;
            return total;
        }
    }

    public static List<RoomEntry> Read(RomFile rom)
        => Read(ModelTextureTable.LoadMainOverlay(rom));

    public static List<RoomEntry> Read(ModelTextureTable.Overlay overlay)
    {
        var rooms = new List<RoomEntry>(TotalRooms);

        var stagePointers = new uint[StageCount];
        for (int stage = 0; stage < StageCount; stage++)
        {
            uint at = overlay.At(RootAddress) + (uint)stage * 4;
            stagePointers[stage] = overlay.Contains(at, 4) ? U32(overlay, at) : 0;
        }

        // Every room's list of backgrounds, so a list's length can be taken from the next one along.
        var pointers = new List<uint>();
        foreach (uint stagePointer in stagePointers)
        {
            if (stagePointer == 0) continue;
            for (int room = 0; room < 64; room++)
            {
                uint at = stagePointer + (uint)room * 4;
                if (!overlay.Contains(at, 4)) break;
                pointers.Add(U32(overlay, at));
            }
        }

        pointers.Sort();

        int flat = 0;
        for (int stage = 0; stage < StageCount; stage++)
        {
            for (int room = 0; room < RoomsPerStage[stage]; room++, flat++)
            {
                var backgrounds = new List<int>();
                uint at = stagePointers[stage] + (uint)room * 4;

                if (overlay.Contains(at, 4))
                {
                    uint list = U32(overlay, at);
                    if (list >= overlay.BaseAddress && list < overlay.At(DataEnd))
                    {
                        uint end = NextAfter(pointers, list, overlay.At(DataEnd));

                        for (uint camera = list; camera + 1 < end && overlay.Contains(camera, 2); camera += 2)
                        {
                            int id = overlay.U16(camera);
                            if (id == 0) break;              // a zero ends the camera list
                            backgrounds.Add(id);
                        }
                    }
                }

                rooms.Add(new RoomEntry(stage, room, flat, backgrounds));
            }
        }

        return rooms;
    }

    /// <summary>Where a room's background list ends: the next list along, or the end of the data.</summary>
    private static uint NextAfter(List<uint> pointers, uint list, uint dataEnd)
    {
        foreach (uint candidate in pointers)
            if (candidate > list && candidate < dataEnd) return candidate;

        return dataEnd;
    }

    private static uint U32(ModelTextureTable.Overlay overlay, uint at)
        => ((uint)overlay.U16(at) << 16) | overlay.U16(at + 2);
}
