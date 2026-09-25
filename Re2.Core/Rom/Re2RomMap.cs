using System;
using System.Collections.Generic;

namespace Re2.Core.Rom;

public enum RegionKind
{
    Header,
    BootCode,
    UncompressedOverlay,
    DeflateChain,
    DataTables,
    Assets,
    Backgrounds,
    Padding
}

public sealed record RomRegion(string Name, int Start, int End, RegionKind Kind, string Notes = "")
{
    public int Length => End - Start;
}

/// <summary>
/// Static layout of Resident Evil 2 (U) [!].z64, established by analysis of the retail ROM.
/// </summary>
public static class Re2RomMap
{
    public const string ExpectedGameCode = "NREE";
    public const int ExpectedLength = 64 * 1024 * 1024;

    /// <summary>Start of the raw-deflate code chain.</summary>
    public const int CodeChainStart = 0x014422;

    /// <summary>The chain never runs past here; scanning stops at this bound.</summary>
    public const int CodeChainLimit = 0x0B7000;

    /// <summary>
    /// End of the overlay region as declared by the game's own table (last entry ends here).
    /// </summary>
    public const int OverlayRegionEnd = 0x0C8624;

    /// <summary>Uncompressed record tables sit between the code chain and the asset blob.</summary>
    public const int DataTablesStart = 0x0B624D;
    public const int DataTablesEnd = 0x0D8000;

    /// <summary>Contiguous run of baseline JFIF backgrounds.</summary>
    public const int BackgroundsStart = 0x2BDEFB2;
    public const int BackgroundsEnd = 0x3AF17FE;

    /// <summary>Last non-zero byte in the retail image; everything after is padding.</summary>
    public const int LastUsedByte = 0x3FD0DF5;

    /// <summary>
    /// Files in RE2's container are packed nose to tail: a file occupies [offset, offset+size),
    /// then <see cref="InterFileGap"/> bytes follow, then the next file starts at the next
    /// <see cref="FileAlignment"/>-byte boundary.
    /// </summary>
    public const int InterFileGap = 8;

    public const int FileAlignment = 2;

    /// <summary>Given a file's offset and size, where the next file in the container begins.</summary>
    public static int NextFileOffset(int offset, int size)
    {
        int end = offset + size + InterFileGap;
        return (end + (FileAlignment - 1)) & ~(FileAlignment - 1);
    }

    public static IReadOnlyList<RomRegion> Regions { get; } = new[]
    {
        new RomRegion("header",        0x0000000, 0x0001000, RegionKind.Header,              "cart id NREE, build stamp RE.093099.1542"),
        new RomRegion("boot",          0x0001000, 0x000FD00, RegionKind.BootCode,            "IPL3-loaded MIPS, uncompressed"),
        new RomRegion("overlay.zlib",  0x000FD00, 0x0014422, RegionKind.UncompressedOverlay, "contains zlib inflate; error strings at 0x12150"),
        new RomRegion("code.deflate",  0x0014422, 0x00B624D, RegionKind.DeflateChain,        "35 raw-deflate overlays incl. the 1.17 MB main binary at 0x80018A80"),
        new RomRegion("overlays.bank2",0x00B624D, 0x00C8624, RegionKind.DeflateChain,        "overlay table entries 39-66; same sizes as bank 1 but not deflate"),
        new RomRegion("tables",        0x00C8624, 0x00D8000, RegionKind.DataTables,          "uncompressed 8-byte records"),
        new RomRegion("assets.a",      0x00D8000, 0x2BDEFB2, RegionKind.Assets,              "models, sound, room data; index not yet recovered"),
        new RomRegion("backgrounds",   0x2BDEFB2, 0x3AF17FE, RegionKind.Backgrounds,         "1227 baseline JFIF images, mostly 320x224 4:2:0"),
        new RomRegion("assets.b",      0x3AF17FE, 0x3FD0DF5, RegionKind.Assets,              "further assets"),
        new RomRegion("padding",       0x3FD0DF5, 0x4000000, RegionKind.Padding,             "zero fill (~192 KB free)")
    };

    public static RomRegion? RegionAt(int offset)
    {
        foreach (var r in Regions)
            if (offset >= r.Start && offset < r.End) return r;
        return null;
    }

    /// <summary>Whether this is the game the tools know.</summary>
    public static bool IsExpectedRom(RomFile rom)
        => rom.GameCode is ExpectedGameCode or "NREP" or "NB5J" && rom.Length >= ExpectedLength;

    /// <summary>True for a ROM that has been grown past the retail cart size.</summary>
    public static bool IsExpanded(RomFile rom) => rom.Length > ExpectedLength;
}
