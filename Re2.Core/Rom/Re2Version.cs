using System;

namespace Re2.Core.Rom;

/// <summary>Which release of the game a ROM is.</summary>
public enum Re2Release
{
    /// <summary>Not a build this tool recognises; treated as Rev 1, the one it was built against.</summary>
    Unknown,

    /// <summary>"Resident Evil 2 (USA)" -- the first USA release, header version 0x00.</summary>
    UsaRev0,

    /// <summary>"Resident Evil 2 (USA) (Rev 1)" -- the later USA release, header version 0x01.</summary>
    UsaRev1
}

/// <summary>The ROM's release, and what it costs the rest of the tool.</summary>
public sealed record Re2VersionInfo(Re2Release Release, string Name, int MainOverlayDelta)
{
    /// <summary>True when the tool knows this build and its tables can be trusted.</summary>
    public bool Recognised => Release != Re2Release.Unknown;

    /// <summary>What to show beside a file name.</summary>
    public override string ToString() => Name;
}

public static class Re2Version
{
    /// <summary>Header CRC1 of each known build, which identifies it exactly.</summary>
    public const uint UsaRev0Crc1 = 0x2F493DD0;
    public const uint UsaRev1Crc1 = 0xAA18B1A5;

    /// <summary>How far Rev 0's main-overlay tables sit below Rev 1's.</summary>
    public const int UsaRev0Delta = -96;

    public static readonly Re2VersionInfo Rev0 = new(Re2Release.UsaRev0, "USA", UsaRev0Delta);
    public static readonly Re2VersionInfo Rev1 = new(Re2Release.UsaRev1, "USA (Rev 1)", 0);

    /// <summary>
    /// Identifies a ROM by its header CRC1, which is unique per build and unaffected by anything this
    /// tool writes -- a rebuilt ROM has its checksum recalculated, so the CRC of an edited Rev 1 is not
    /// Rev 1's.
    /// </summary>
    public static Re2VersionInfo Detect(RomFile rom)
    {
        uint crc1 = rom.Crc1;
        if (crc1 == UsaRev1Crc1) return Rev1;
        if (crc1 == UsaRev0Crc1) return Rev0;

        // An edited ROM: the game code still has to match before the version byte means anything.
        if (rom.GameCode.StartsWith("NRE", StringComparison.Ordinal))
        {
            if (rom.Version == 0x01) return Rev1 with { Name = "USA (Rev 1), edited" };
            if (rom.Version == 0x00) return Rev0 with { Name = "USA, edited" };
        }

        return new Re2VersionInfo(Re2Release.Unknown, "unrecognised build", 0);
    }
}
