using System;
using System.Collections.Generic;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>Which mask -- the foreground layer -- each camera angle uses.</summary>
public static class MaskTable
{
    public const uint RootAddress = 0x801122E4;

    /// <summary>A camera with no foreground stores this instead of an asset id.</summary>
    public const int None = 0xFFFF;

    /// <summary>
    /// Mask asset ids per room, in camera order, indexed the same way <see cref="RoomTable"/> is.
    /// </summary>
    public static List<int[]> Read(RomFile rom)
        => Read(ModelTextureTable.LoadMainOverlay(rom));

    public static List<int[]> Read(ModelTextureTable.Overlay overlay)
    {
        var perRoom = new List<int[]>(RoomTable.TotalRooms);

        var stagePointers = new uint[RoomTable.StageCount];
        for (int stage = 0; stage < RoomTable.StageCount; stage++)
        {
            uint at = overlay.At(RootAddress) + (uint)stage * 4;
            stagePointers[stage] = overlay.Contains(at, 4) ? U32(overlay, at) : 0;
        }

        // The data ends where the first stage's array of room pointers begins.
        uint dataEnd = uint.MaxValue;
        foreach (uint pointer in stagePointers)
            if (pointer != 0) dataEnd = Math.Min(dataEnd, pointer);

        var lists = new List<uint>();
        for (int stage = 0; stage < RoomTable.StageCount; stage++)
        {
            if (stagePointers[stage] == 0) continue;
            for (int room = 0; room < RoomTable.RoomsPerStage[stage]; room++)
            {
                uint at = stagePointers[stage] + (uint)room * 4;
                if (overlay.Contains(at, 4)) lists.Add(U32(overlay, at));
            }
        }

        lists.Sort();

        for (int stage = 0; stage < RoomTable.StageCount; stage++)
        {
            for (int room = 0; room < RoomTable.RoomsPerStage[stage]; room++)
            {
                var cameras = new List<int>();
                uint at = stagePointers[stage] + (uint)room * 4;

                if (overlay.Contains(at, 4))
                {
                    uint list = U32(overlay, at);

                    if (list >= overlay.BaseAddress && list < dataEnd)
                    {
                        // A list runs to the next one along, as everywhere else in this game.
                        uint end = dataEnd;
                        foreach (uint candidate in lists)
                            if (candidate > list && candidate < dataEnd) { end = candidate; break; }

                        for (uint camera = list; camera + 1 < end && overlay.Contains(camera, 2); camera += 2)
                        {
                            int id = overlay.U16(camera);
                            cameras.Add(id is None or 0 ? -1 : id);
                        }
                    }
                }

                perRoom.Add(cameras.ToArray());
            }
        }

        return perRoom;
    }

    private static uint U32(ModelTextureTable.Overlay overlay, uint at)
        => ((uint)overlay.U16(at) << 16) | overlay.U16(at + 2);
}
