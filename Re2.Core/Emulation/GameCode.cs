using System;
using Re2.Core.Rom;

namespace Re2.Core.Emulation;

/// <summary>Loads the cart's code into an interpreter, at the addresses it runs at.</summary>
public static class GameCode
{
    /// <summary>Where the boot segment's RAM image begins in the ROM.</summary>
    public const int BootRomBase = 0xB80;

    public const uint RamBase = 0x80000000;

    /// <summary>The boot segment runs up to where the first compressed overlay begins.</summary>
    public const int BootSize = 0x14420 - BootRomBase;

    private const int MainOverlayIndex = 1;

    public static MipsCpu Load(RomFile rom)
    {
        var cpu = new MipsCpu { Layout = rom.Layout };

        var entries = OverlayTable.Read(rom.Data);

        // The boot segment runs up to overlay 1, which is 0x90 further on in Japan.
        var first = entries.Find(e => e.Index == MainOverlayIndex);
        int bootSize = first is null ? BootSize : first.RomOffset - BootRomBase;
        cpu.Load(RamBase, rom.Data.AsSpan(BootRomBase, bootSize));

        foreach (var entry in entries)
        {
            if (entry.Index != MainOverlayIndex) continue;
            if (!OverlayTable.TryDecompress(rom.Data, entry, out var image))
                throw new InvalidOperationException("The main overlay would not decompress.");

            cpu.Load(entry.LoadAddress, image);
            return cpu;
        }

        throw new InvalidOperationException("The main overlay is not in the overlay table.");
    }

    /// <summary>The game's <c>memset</c>-to-zero, used here to check the interpreter against real code.</summary>
    public const uint Memset = 0x80006650;
}
