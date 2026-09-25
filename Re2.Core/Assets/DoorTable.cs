using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>
/// One door: where it sits in its own room, and where the player ends up on the other side.
/// </summary>
public sealed record Door(
    int FromRoom,
    int Slot,
    short DestX, short DestY, short DestZ, short DestAngle,
    int DestStage, int DestRoom, int DestCamera, int DestFloor,
    int Model,
    byte Lock,
    int KeyItem)
{
    /// <summary>Whether opening this door is gated on a flag (a key, or story progress).</summary>
    public bool IsLocked => (Lock & 0x80) != 0;

    /// <summary>Which bit of the door-flag bitfield at 0x800E8EC0 unlocks it.</summary>
    public int LockFlag => Lock & 0x3F;

    public override string ToString()
        => $"room {FromRoom} -> {DestStage:X2}-{DestRoom:X2} cam {DestCamera} at ({DestX},{DestY},{DestZ})";
}

/// <summary>Every door in the game, read out of the rooms' init scripts.</summary>
public static class DoorTable
{
    /// <summary>The room init scripts: asset <c>ScriptBase + flatRoomIndex</c>.</summary>
    public const int ScriptBase = 3379;

    public const byte DoorOpcode = 0x3B;
    public const int OpcodeLength = 0x20;

    /// <summary>The AOT type the door handler is registered for.</summary>
    public const int DoorAotType = 1;

    private const int RecordOffset = 0x0E;

    /// <summary>Every door in the game, in room order.</summary>
    public static List<Door> Read(RomFile rom) => Read(rom, AssetDirectory.Read(rom), RoomTable.Read(rom));

    public static List<Door> Read(RomFile rom, AssetDirectory directory, IReadOnlyList<RoomEntry> rooms)
    {
        var doors = new List<Door>();

        for (int flat = 0; flat < rooms.Count; flat++)
        {
            var script = Script(rom, directory, rom.Layout.DoorScriptBase + flat);
            if (script.IsEmpty) continue;

            for (int pc = 0; pc + OpcodeLength <= script.Length; pc++)
            {
                if (script[pc] != DoorOpcode) continue;
                if (script[pc + 1] >= 0x20) continue;              // AOT slots run 0..31
                if (script[pc + 2] != DoorAotType) continue;

                var door = Parse(script, pc, flat, rooms);
                if (door is not null) doors.Add(door);
            }
        }

        return doors;
    }

    /// <summary>Entry points into each room, indexed the same way <see cref="RoomTable"/> is.</summary>
    public static List<List<Door>> EntrancesByRoom(RomFile rom)
    {
        var rooms = RoomTable.Read(rom);
        var entrances = new List<List<Door>>(rooms.Count);
        for (int i = 0; i < rooms.Count; i++) entrances.Add(new List<Door>());

        foreach (var door in Read(rom, AssetDirectory.Read(rom), rooms))
        {
            int flat = FlatIndex(door.DestStage, door.DestRoom);
            if (flat >= 0 && flat < entrances.Count) entrances[flat].Add(door);
        }

        return entrances;
    }

    public static int FlatIndex(int stage, int room)
    {
        if (stage < 0 || stage >= RoomTable.StageCount) return -1;
        if (room < 0 || room >= RoomTable.RoomsPerStage[stage]) return -1;

        int flat = 0;
        for (int s = 0; s < stage; s++) flat += RoomTable.RoomsPerStage[s];
        return flat + room;
    }

    private static Door? Parse(ReadOnlySpan<byte> script, int pc, int fromRoom, IReadOnlyList<RoomEntry> rooms)
    {
        int at = pc + RecordOffset;

        int stage = script[at + 8], room = script[at + 9], camera = script[at + 10], floor = script[at + 11];

        // A door has to lead somewhere that exists.
        int flat = FlatIndex(stage, room);
        if (flat < 0) return null;
        if (rooms[flat].ViewCount == 0 || camera >= rooms[flat].ViewCount) return null;
        if (floor > 4) return null;

        return new Door(
            fromRoom, script[pc + 1],
            BinaryPrimitives.ReadInt16BigEndian(script[at..]),
            BinaryPrimitives.ReadInt16BigEndian(script[(at + 2)..]),
            BinaryPrimitives.ReadInt16BigEndian(script[(at + 4)..]),
            BinaryPrimitives.ReadInt16BigEndian(script[(at + 6)..]),
            stage, room, camera, floor,
            script[at + 0x0C],
            script[at + 0x0F],
            script[at + 0x10]);
    }

    private static ReadOnlySpan<byte> Script(RomFile rom, AssetDirectory directory, int assetId)
    {
        foreach (var entry in directory.Entries)
            if (entry.Index == assetId && directory.TryGetData(rom, entry, out var data))
                return data;

        return ReadOnlySpan<byte>.Empty;
    }
}
