using System;
using System.Collections.Generic;
using Re2.Core.Formats;

namespace Re2.Core.Rom;

/// <summary>
/// Where one release keeps the things the tool looks up by fixed address or asset id. Everything is
/// keyed by its USA Rev 1 value, which is what the rest of the code is written against.
/// </summary>
public sealed class Re2Layout
{
    /// <summary>Uniform shift of overlay-1 addresses, used when <see cref="Moved"/> has no entry.</summary>
    public int MainOverlayDelta { get; init; }

    /// <summary>Per-address moves, for builds whose tables did not all shift by the same amount.</summary>
    public IReadOnlyDictionary<uint, uint>? Moved { get; init; }

    /// <summary>Shift of the resident RAM variables the GameShark codes poke.</summary>
    public int RamDelta { get; init; }

    public int VoiceBankAsset { get; init; } = 1979;
    public int SoundProjectAsset { get; init; } = 1980;
    public int SoundPoolAsset { get; init; } = 1981;
    public int SampleDirectoryAsset { get; init; } = 1982;
    public int SampleDataAsset { get; init; } = 1983;

    /// <summary>Room init scripts: asset <c>DoorScriptBase + flatRoomIndex</c>.</summary>
    public int DoorScriptBase { get; init; } = 3379;

    public int IconFirstAsset { get; init; } = 5336;
    public int IconBundleAsset { get; init; } = 5334;
    public int IconPaletteAsset { get; init; } = 5329;

    public int SceneryFirstAsset { get; init; } = 7875;
    public int SceneryLastAsset { get; init; } = 8083;

    /// <summary>
    /// The asset ids of the document pages when they are pictures rather than text (Japan's are run-
    /// length coded 4-level images; see <see cref="Formats.JapaneseDocument"/>), or null.
    /// </summary>
    public (int First, int Last)? DocumentPages { get; init; }

    /// <summary>Whether this asset is one of the picture document pages.</summary>
    public bool IsDocumentPage(int id) => DocumentPages is { } r && id >= r.First && id <= r.Last;

    /// <summary>Whether the documents are Latin-1 (Europe's French accents) rather than plain ASCII.</summary>
    public bool Latin1Documents { get; init; }

    /// <summary>
    /// The second-language inventory text (French in Europe, Japanese in Japan), or null when there
    /// is none to edit.
    /// </summary>
    public InventoryTextAddresses? AlternateInventoryText { get; init; }

    /// <summary>
    /// Runs of this build's asset ids that hold the same content as Rev 1's, as (first, last, delta
    /// to Rev 1). Null for the USA builds, which share Rev 1's numbering.
    /// </summary>
    public IReadOnlyList<(int First, int Last, int Delta)>? Rev1IdRuns { get; init; }

    /// <summary>The Rev 1 id of the same asset, or -1 when Rev 1 has no counterpart.</summary>
    public int ToRev1AssetId(int id)
    {
        if (Rev1IdRuns is null) return id;
        foreach (var (first, last, delta) in Rev1IdRuns)
            if (id >= first && id <= last) return id + delta;
        return -1;
    }

    /// <summary>Backgrounds from here on are this build's own; those before match Rev 1's by index.</summary>
    public int FirstExtraBackground { get; init; } = int.MaxValue;

    /// <summary>
    /// The voice clip this build inserts ahead of Rev 1's numbering, or null when it has none. Clips
    /// before it match Rev 1 by index; clips after it are Rev 1's shifted up by one.
    /// </summary>
    public int? InsertedVoiceClip { get; init; }

    /// <summary>The Rev 1 background index of the same scene, or -1 when Rev 1 has no counterpart.</summary>
    public int ToRev1Background(int index) => index < FirstExtraBackground ? index : -1;

    /// <summary>The Rev 1 voice clip of the same line, or -1 when Rev 1 has no counterpart.</summary>
    public int ToRev1VoiceClip(int index)
        => InsertedVoiceClip is not int inserted ? index
         : index < inserted ? index
         : index == inserted ? -1
         : index - 1;

    /// <summary>A Rev 1 overlay-1 address, moved to where this build keeps it.</summary>
    public uint Address(uint rev1Address)
    {
        if (Moved is null) return (uint)(rev1Address + MainOverlayDelta);
        if (Moved.TryGetValue(rev1Address, out uint moved)) return moved;
        throw new InvalidOperationException($"No known address in this build for Rev 1 0x{rev1Address:X8}.");
    }

    public static readonly Re2Layout UsaRev1 = new();

    public static readonly Re2Layout UsaRev0 = new() { MainOverlayDelta = -0x60, RamDelta = -0x60 };

    /// <summary>
    /// Europe (En,Fr). Found by aligning its overlay 1 against Rev 1 and following the code that
    /// references each table; see the specification's Europe section.
    /// </summary>
    public static readonly Re2Layout Europe = new()
    {
        RamDelta = -0x3500,
        Latin1Documents = true,
        Moved = new Dictionary<uint, uint>
        {
            [0x800FC900] = 0x800F1AC0,   // examine messages: text
            [0x800FDFA0] = 0x800F3160,   //   table
            [0x800FE088] = 0x800F3248,   //   table end
            [0x800FACB0] = 0x800F5A34,   // item names
            [0x800FB3F8] = 0x800F617C,   //   table
            [0x800FB530] = 0x800F62B4,   //   table end
            [0x801122E4] = 0x80108BE4,   // mask root
            [0x8011C188] = 0x80112658,   // room data end
            [0x8011C3B0] = 0x80112880,   // room root
            [0x80126C80] = 0x8011D150,   // character asset table
            [0x801273B0] = 0x8011D880,   // item table
            [0x801275C0] = 0x8011DA90,   // pair table
            [0x801287B0] = 0x8011EC80,   // entity tables
            [0x80128A10] = 0x8011EEE0,
            [0x80128C70] = 0x8011F140,
            [0x80128ED0] = 0x8011F3A0,
            [0x8012BA84] = 0x80122074,   // scenery props: root
            [0x8012BA8C] = 0x8012207C,   //   global list
            [0x800B53F4] = 0x800B2664,   // MORT block decoder
        },
        // Measured by matching every asset's decoded content against Rev 1. The gaps are what
        // Europe has of its own: the French documents, menus and item text, and the movies.
        // 4276-4381 are Europe's own too; 4342 alone happens to equal Rev 1's 3881 (20 bytes), which
        // once pulled the fifth run back to 4342 and gave 40 assets the same Rev 1 id as 4236-4275.
        Rev1IdRuns = new (int, int, int)[]
        {
            (225, 2646, -224), (3094, 4168, -460), (4169, 4186, -354), (4188, 4275, -355),
            (4382, 5781, -461), (5783, 5783, -462), (5785, 5786, -463), (5794, 5795, -469),
            (5799, 5799, -472), (5801, 5803, -473), (5805, 5805, -474), (5807, 6044, -475),
            (6050, 6068, -480), (6092, 8590, -500),
        },
        FirstExtraBackground = 1227,
        InsertedVoiceClip = 32,
        VoiceBankAsset = 2203,
        SoundProjectAsset = 2204,
        SoundPoolAsset = 2205,
        SampleDirectoryAsset = 2206,
        SampleDataAsset = 2207,
        DoorScriptBase = 3839,
        IconFirstAsset = 5811,
        IconBundleAsset = 5809,
        IconPaletteAsset = 5802,
        SceneryFirstAsset = 8375,
        SceneryLastAsset = 8583,
        AlternateInventoryText = new InventoryTextAddresses(
            NamesAddress: 0x800F5038, NamesTable: 0x800F58FC, NamesTableEnd: 0x800F5A34,
            MessagesText: 0x800F38BC, MessagesTable: 0x800F4F50, MessagesTableEnd: 0x800F5038,
            Language: "French", FileSuffix: "fr", Charset: ItemCharset.French)
    };

    /// <summary>
    /// Japan (Biohazard 2). Found the same way as Europe's. Its overlay 1 loads 0x90 higher, at
    /// 0x80018B10, but every address here is absolute, so that changes nothing.
    /// </summary>
    public static readonly Re2Layout Japan = new()
    {
        RamDelta = -0x3A80,
        Moved = new Dictionary<uint, uint>
        {
            [0x800FC900] = 0x800F1550,   // examine messages (English): text
            [0x800FDFA0] = 0x800F2BF0,   //   table
            [0x800FE088] = 0x800F2CD8,   //   table end
            [0x800FACB0] = 0x800F4C74,   // item names (English)
            [0x800FB3F8] = 0x800F53BC,   //   table
            [0x800FB530] = 0x800F54F4,   //   table end
            [0x801122E4] = 0x80107E24,   // mask root
            [0x8011C188] = 0x801118B8,   // room data end
            [0x8011C3B0] = 0x80111AE0,   // room root
            [0x80126C80] = 0x8011C3B0,   // character asset table
            [0x801273B0] = 0x8011CAE0,   // item table
            [0x801275C0] = 0x8011CCF0,   // pair table
            [0x801287B0] = 0x8011DEE0,   // entity tables
            [0x80128A10] = 0x8011E140,
            [0x80128C70] = 0x8011E3A0,
            [0x80128ED0] = 0x8011E600,
            [0x8012BA84] = 0x801211B4,   // scenery props: root
            [0x8012BA8C] = 0x801211BC,   //   global list
            [0x800B53F4] = 0x800B2084,   // MORT block decoder
        },
        // Japan keeps Rev 1's numbering up to 2126, then inserts its own documents and menus.
        Rev1IdRuns = new (int, int, int)[]
        {
            (1, 2126, 0), (2295, 2577, 56), (2815, 3889, -181), (3997, 5394, -76),
            (5404, 5408, -79), (5412, 5415, -80), (5422, 8176, -86),
        },
        DocumentPages = (2127, 2294),
        InsertedVoiceClip = 32,
        DoorScriptBase = 3560,
        IconFirstAsset = 5422,
        IconBundleAsset = 5414,
        IconPaletteAsset = 5408,
        SceneryFirstAsset = 7961,
        SceneryLastAsset = 8169,
        AlternateInventoryText = new InventoryTextAddresses(
            NamesAddress: 0x800F457C, NamesTable: 0x800F4B3C, NamesTableEnd: 0x800F4C74,
            MessagesText: 0x800F31AC, MessagesTable: 0x800F4494, MessagesTableEnd: 0x800F457C,
            Language: "Japanese", FileSuffix: "ja", Charset: ItemCharset.Japanese)
    };
}

/// <summary>Where one language's item names and examine messages live in overlay 1.</summary>
public sealed record InventoryTextAddresses(
    uint NamesAddress, uint NamesTable, uint NamesTableEnd,
    uint MessagesText, uint MessagesTable, uint MessagesTableEnd,
    string Language, string FileSuffix, ItemCharset Charset);
