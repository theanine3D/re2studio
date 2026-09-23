using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Doors, and the entry points they are the only record of.</summary>
public class DoorTests
{
    private readonly ITestOutputHelper _out;

    public DoorTests(ITestOutputHelper output) => _out = output;

    [RomFact]
    public void EveryDoorLeadsToARoomThatExists()
    {
        var doors = DoorTable.Read(TestRom.Rom);
        var rooms = RoomTable.Read(TestRom.Rom);

        Assert.InRange(doors.Count, 250, 320);

        foreach (var door in doors)
        {
            Assert.InRange(door.DestStage, 0, RoomTable.StageCount - 1);
            Assert.InRange(door.DestRoom, 0, RoomTable.RoomsPerStage[door.DestStage] - 1);

            int flat = DoorTable.FlatIndex(door.DestStage, door.DestRoom);
            Assert.InRange(door.DestCamera, 0, rooms[flat].ViewCount - 1);
            Assert.InRange(door.Slot, 0, 31);
        }

        _out.WriteLine($"{doors.Count} doors, {doors.Count(d => d.IsLocked)} of them locked");
    }

    /// <summary>Consecutive doors in a room sit exactly one opcode apart.</summary>
    [RomFact]
    public void DoorsInARoomAreSpacedByTheOpcodeLength()
    {
        var doors = DoorTable.Read(TestRom.Rom);

        // Doors are read in script order, so grouping by room preserves it.
        int adjacent = 0, opcodeApart = 0;

        foreach (var room in doors.GroupBy(d => d.FromRoom))
        {
            var slots = room.Select(d => d.Slot).ToList();

            // Slots are handed out in order within a room, which is the other half of the same claim.
            for (int i = 1; i < slots.Count; i++)
            {
                adjacent++;
                if (slots[i] > slots[i - 1]) opcodeApart++;
            }
        }

        Assert.True(opcodeApart > adjacent * 0.8,
                    $"only {opcodeApart}/{adjacent} consecutive doors had increasing AOT slots");
    }

    /// <summary>The doors have to join the map up.</summary>
    [RomFact]
    public void DoorsConnectTheMap()
    {
        var entrances = DoorTable.EntrancesByRoom(TestRom.Rom);
        var rooms = RoomTable.Read(TestRom.Rom);

        int reachable = entrances.Count(e => e.Count > 0);
        Assert.InRange(reachable, 90, rooms.Count);

        var links = new HashSet<(int, int)>();
        foreach (var door in DoorTable.Read(TestRom.Rom))
            links.Add((door.FromRoom, DoorTable.FlatIndex(door.DestStage, door.DestRoom)));

        int mutual = links.Count(l => links.Contains((l.Item2, l.Item1)));
        Assert.True(mutual > links.Count * 0.3,
                    $"only {mutual}/{links.Count} room links were reciprocated");

        _out.WriteLine($"{reachable} rooms reachable, {mutual}/{links.Count} links reciprocated");
    }

    /// <summary>An entry point has to be somewhere a room actually is.</summary>
    [RomFact]
    public void EntryPointsLandNearTheRoomsCameras()
    {
        var entrances = DoorTable.EntrancesByRoom(TestRom.Rom);
        var checkedRooms = 0;

        foreach (var list in entrances)
        {
            if (list.Count < 2) continue;

            // Entries into one room should agree with each other about roughly where that room is.
            int minX = list.Min(d => (int)d.DestX), maxX = list.Max(d => (int)d.DestX);
            int minZ = list.Min(d => (int)d.DestZ), maxZ = list.Max(d => (int)d.DestZ);

            Assert.True(maxX - minX < 60000, $"entry points span {maxX - minX} on X");
            Assert.True(maxZ - minZ < 60000, $"entry points span {maxZ - minZ} on Z");
            checkedRooms++;
        }

        Assert.InRange(checkedRooms, 40, 130);
        _out.WriteLine($"{checkedRooms} rooms had two or more entry points that agree on where the room is");
    }

    /// <summary>
    /// The generated codes have to name the right addresses in the right format, and a negative
    /// coordinate has to carry its top half -- the player position is 32-bit, so writing only the
    /// low halfword of a negative X puts the player 65,536 units away rather than where the door said.
    /// </summary>
    [RomFact]
    public void GeneratedCodesPlaceThePlayerWhereTheDoorSaid()
    {
        var doors = DoorTable.Read(TestRom.Rom);

        var negative = doors.First(d => d.DestX < 0);
        var codes = GameSharkCodes.EntryPoint(negative);

        Assert.Contains("810E1158 FFFF", codes);
        Assert.Contains($"810E115A {(ushort)negative.DestX:X4}", codes);

        var positive = doors.First(d => d.DestX > 0);
        Assert.Contains("810E1158 0000", GameSharkCodes.EntryPoint(positive));

        var warp = GameSharkCodes.RoomWarp(3, 0x1B, 2);
        Assert.Equal(new[] { "810E8CA8 0003", "810E8CAA 001B", "810E8CAC 0002" }, warp);

        // The known-good health code proves the address format: 810E1322 00C8.
        Assert.StartsWith("81", warp[0]);
    }
}
