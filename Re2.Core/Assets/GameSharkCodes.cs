using System.Collections.Generic;
using System.Text;

namespace Re2.Core.Assets;

/// <summary>GameShark codes that put the player in a chosen room, at a real entry point.</summary>
public static class GameSharkCodes
{
    // Overlay 1 data, which loads at a fixed address and stays resident.
    public const uint Stage = 0x800E8CA8;
    public const uint Room = 0x800E8CAA;
    public const uint Camera = 0x800E8CAC;
    public const uint PlayerX = 0x800E1158;
    public const uint PlayerY = 0x800E115C;
    public const uint PlayerZ = 0x800E1160;
    public const uint PlayerAngle = 0x800E1196;
    public const uint PlayerFloor = 0x800E12D2;

    /// <summary>Codes that select the room.</summary>
    public static List<string> RoomWarp(int stage, int room, int camera, int delta = 0) => new()
    {
        Halfword(At(Stage, delta), (ushort)stage),
        Halfword(At(Room, delta), (ushort)room),
        Halfword(At(Camera, delta), (ushort)camera),
    };

    private static uint At(uint rev1Address, int delta) => (uint)(rev1Address + delta);

    /// <summary>Codes that place the player.</summary>
    public static List<string> EntryPoint(Door door, bool onButton = false, int delta = 0) => new()
    {
        Halfword(At(PlayerX, delta), High(door.DestX), onButton),
        Halfword(At(PlayerX + 2, delta), Low(door.DestX), onButton),
        Halfword(At(PlayerY, delta), High(door.DestY), onButton),
        Halfword(At(PlayerY + 2, delta), Low(door.DestY), onButton),
        Halfword(At(PlayerZ, delta), High(door.DestZ), onButton),
        Halfword(At(PlayerZ + 2, delta), Low(door.DestZ), onButton),
        Halfword(At(PlayerAngle, delta), (ushort)door.DestAngle, onButton),
        Byte(At(PlayerFloor, delta), (byte)door.DestFloor, onButton),
    };

    /// <summary>
    /// The codes in their two groups, so each can be shown -- and copied -- on its own.
    /// </summary>
    public static List<(string Title, List<string> Codes)> Groups(Door door, int delta = 0) => new()
    {
        ($"Warp to {door.DestStage:X2}-{door.DestRoom:X2}",
         RoomWarp(door.DestStage, door.DestRoom, door.DestCamera, delta)),

        ("Entry point -- hold the cheat button once, after the room loads",
         EntryPoint(door, onButton: true, delta)),

        ("Entry point, fallback for emulators without a cheat button",
         EntryPoint(door, delta: delta)),
    };

    /// <summary>Both halves, labelled, ready to paste into an emulator's cheat list.</summary>
    public static string Describe(Door door, int delta = 0)
    {
        var text = new StringBuilder();

        foreach (var (title, codes) in Groups(door, delta))
        {
            if (text.Length > 0) text.AppendLine();
            text.AppendLine($"[{title}]");
            foreach (string code in codes) text.AppendLine(code);
        }

        return text.ToString();
    }

    /// <summary>The player's position is a 32-bit signed value, so a negative one needs its top half too.</summary>
    private static ushort High(short value) => (ushort)(value < 0 ? 0xFFFF : 0x0000);

    private static ushort Low(short value) => (ushort)value;

    // 80/81 write every frame; 88/89 write only while the cheat button is held.
    private static string Halfword(uint address, ushort value, bool onButton = false)
        => $"{(onButton ? 89 : 81)}{address & 0xFFFFFF:X6} {value:X4}";

    private static string Byte(uint address, byte value, bool onButton = false)
        => $"{(onButton ? 88 : 80)}{address & 0xFFFFFF:X6} 00{value:X2}";
}
