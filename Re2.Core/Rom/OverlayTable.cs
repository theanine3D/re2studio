using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Re2.Core.Codecs;

namespace Re2.Core.Rom;

/// <summary>One 16-byte record in the game's overlay table.</summary>
public sealed record OverlayEntry(int Index, uint CartAddress, int CompressedSize, uint LoadAddress, uint EndAddress)
{
    /// <summary>ROM file offset, with the PI bus base stripped off.</summary>
    public int RomOffset => (int)(CartAddress & 0x0FFFFFFF);

    /// <summary>Size once decompressed, implied by the load/end pair.</summary>
    public int DecompressedSize => (int)(EndAddress - LoadAddress);

    /// <summary>Empty slots exist in the table; the loader treats them as no-ops.</summary>
    public bool IsEmpty => CompressedSize == 0 || DecompressedSize == 0;

    /// <summary>Offset of the payload proper, past the 2-byte block header.</summary>
    public int PayloadOffset => RomOffset + OverlayTable.BlockHeaderSize;

    public override string ToString() =>
        $"#{Index} ROM 0x{RomOffset:X7} {CompressedSize:N0}B -> 0x{LoadAddress:X8}..0x{EndAddress:X8} ({DecompressedSize:N0}B)";
}

/// <summary>The game's own table of code overlays, at RAM 0x80012150 (ROM 0x12CD0).</summary>
public static class OverlayTable
{
    /// <summary>ROM offset of the table. RAM 0x80012150 maps here via the boot block.</summary>
    public const int TableRomOffset = 0x12CD0;

    public const uint TableRamAddress = 0x80012150;

    public const int RecordSize = 0x10;

    public const string Magic = "OVERLAYTABLENUMC";

    /// <summary>Each compressed block starts with two bytes before the deflate stream proper.</summary>
    public const int BlockHeaderSize = 2;

    private static readonly uint CartBusBase = 0xB0000000;
    private static readonly uint CartBusLimit = 0xB4000000;

    /// <summary>Cached, uncached-alias-free RDRAM window that every load address falls in.</summary>
    private const uint KernelSegment0 = 0x80000000;
    private const uint KernelSegment0Limit = 0x80800000;

    /// <summary>True when the magic record is present.</summary>
    public static bool IsPresent(ReadOnlySpan<byte> rom) => Locate(rom) >= 0;

    /// <summary>
    /// ROM offset of the table, found by its magic: 0x12CD0 in the USA builds, 0x12CB0 in Europe.
    /// -1 when there is none.
    /// </summary>
    public static int Locate(ReadOnlySpan<byte> rom)
    {
        if (HasMagic(rom, TableRomOffset)) return TableRomOffset;
        for (int at = 0x10000; at < 0x14420 && at + RecordSize <= rom.Length; at += RecordSize)
            if (HasMagic(rom, at)) return at;
        return -1;
    }

    private static bool HasMagic(ReadOnlySpan<byte> rom, int at)
        => at + RecordSize <= rom.Length && Encoding.ASCII.GetString(rom.Slice(at, RecordSize)) == Magic;

    /// <summary>
    /// Reads every entry until a record whose cart address is out of range, which terminates the table.
    /// </summary>
    public static List<OverlayEntry> Read(ReadOnlySpan<byte> rom)
    {
        var entries = new List<OverlayEntry>();
        int table = Locate(rom);
        if (table < 0) return entries;

        for (int index = 1; ; index++)
        {
            int at = table + index * RecordSize;
            if (at + RecordSize > rom.Length) break;

            uint cart = BinaryPrimitives.ReadUInt32BigEndian(rom.Slice(at, 4));
            if (cart < CartBusBase || cart >= CartBusLimit) break;

            uint load = BinaryPrimitives.ReadUInt32BigEndian(rom.Slice(at + 8, 4));

            // The table is terminated by a filler record (0xCDCDCDCD sizes, zero addresses) whose
            // cart word still happens to fall in range, so check for a usable load address too.
            if (load < KernelSegment0 || load >= KernelSegment0Limit) break;

            entries.Add(new OverlayEntry(
                index,
                cart,
                (int)BinaryPrimitives.ReadUInt32BigEndian(rom.Slice(at + 4, 4)),
                load,
                BinaryPrimitives.ReadUInt32BigEndian(rom.Slice(at + 12, 4))));
        }

        return entries;
    }

    /// <summary>
    /// Inflates an overlay, verifying it produces exactly the size the table declares.
    /// </summary>
    public static bool TryDecompress(ReadOnlySpan<byte> rom, OverlayEntry entry, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (entry.IsEmpty) return false;

        int start = entry.PayloadOffset;
        int available = Math.Min(entry.CompressedSize + 64, rom.Length - start);
        if (start < 0 || available <= 0) return false;

        if (!RawDeflate.TryDecompress(rom.Slice(start, available), out var output, out _)) return false;
        if (output.Length != entry.DecompressedSize) return false;

        data = output;
        return true;
    }

    /// <summary>Entries that decompress as deflate, i.e. the ones we can currently map and analyse.</summary>
    public static List<OverlayEntry> DeflateEntries(ReadOnlySpan<byte> rom, List<OverlayEntry> entries)
    {
        var usable = new List<OverlayEntry>();
        foreach (var entry in entries)
            if (TryDecompress(rom, entry, out _))
                usable.Add(entry);
        return usable;
    }
}
