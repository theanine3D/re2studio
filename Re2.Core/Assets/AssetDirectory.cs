using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Re2.Core.Codecs;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>Storage codec for an asset, taken from the high half of its trailer tag.</summary>
public enum AssetKind
{
    /// <summary>Stored verbatim. Includes every prerendered background (raw baseline JFIF).</summary>
    Stored = 1,

    /// <summary>PackBits over bytes. See <see cref="PackBits.DecodeBytes"/>.</summary>
    Rle = 2,

    /// <summary>PackBits over big-endian 16-bit units.</summary>
    Rle16 = 4,

    /// <summary>Raw deflate behind a 2-byte block header, same as the code overlays.</summary>
    Deflate = 16
}

public sealed record AssetEntry(
    int Index,
    int Offset,
    int StoredSize,
    int RomOffset,
    AssetKind Kind,
    int DecompressedSize)
{
    /// <summary>Stored payload is padded to an even length before the trailer.</summary>
    public int PaddedSize => StoredSize + (StoredSize & 1);

    public int TrailerRomOffset => RomOffset + PaddedSize;

    public bool IsCompressed => Kind is AssetKind.Deflate or AssetKind.Rle or AssetKind.Rle16;

    /// <summary>True when we can currently produce the decoded bytes for this asset.</summary>
    /// <summary>Every codec the container uses is now implemented, so this holds for all four kinds.</summary>
    public bool IsDecodable => Kind is AssetKind.Stored or AssetKind.Deflate or AssetKind.Rle or AssetKind.Rle16;

    public override string ToString() =>
        $"#{Index} ROM 0x{RomOffset:X7} {Kind} {StoredSize:N0}B -> {DecompressedSize:N0}B";
}

/// <summary>
/// The game's asset directory: 8,091 files covering every background, model, texture and sound.
/// </summary>
public sealed class AssetDirectory
{
    /// <summary>Overlay-table slot whose cart address points at the directory.</summary>
    public const int DirectoryOverlayIndex = 0x43;

    public const int HeaderSize = 0x28;
    public const int RecordSize = 8;
    public const int TrailerSize = 8;

    /// <summary>Two-byte block header ahead of the deflate stream, as used by the code overlays.</summary>
    public const int DeflateBlockHeaderSize = 2;

    private const uint NullOffset = 0xFFFFFFFF;

    public int AssetBaseRomOffset { get; }
    public int DeclaredFileCount { get; }

    /// <summary>Build stamp embedded in the 0x28-byte header, e.g. "Tue Sep 28 02:31:44 1999".</summary>
    public string BuildStamp { get; }

    /// <summary>Every non-null entry. Indices are the file ids the game itself uses.</summary>
    public IReadOnlyList<AssetEntry> Entries { get; }

    /// <summary>Count of null (0xFFFFFFFF) slots, which the loader treats as "no file".</summary>
    public int NullSlotCount { get; }

    private AssetDirectory(int baseOffset, int count, string stamp, List<AssetEntry> entries, int nullSlots)
    {
        AssetBaseRomOffset = baseOffset;
        DeclaredFileCount = count;
        BuildStamp = stamp;
        Entries = entries;
        NullSlotCount = nullSlots;
    }

    private static uint U32(ReadOnlySpan<byte> rom, int offset) => BinaryPrimitives.ReadUInt32BigEndian(rom.Slice(offset, 4));

    /// <summary>Locates the directory via the overlay table and parses it.</summary>
    public static AssetDirectory Read(RomFile rom)
    {
        int table = OverlayTable.Locate(rom.Data);
        if (table < 0) throw new InvalidOperationException("No overlay table in this ROM.");
        int at = table + DirectoryOverlayIndex * OverlayTable.RecordSize;
        if (at + OverlayTable.RecordSize > rom.Length)
            throw new InvalidOperationException("Overlay table does not extend to the directory record.");

        uint cart = U32(rom.Data, at);
        int baseOffset = (int)(cart & 0x0FFFFFFF);
        return ReadAt(rom, baseOffset);
    }

    /// <summary>Parses a directory at a known base, for tooling that wants to override the lookup.</summary>
    public static AssetDirectory ReadAt(RomFile rom, int baseOffset)
    {
        if (baseOffset < 0 || baseOffset + HeaderSize > rom.Length)
            throw new ArgumentOutOfRangeException(nameof(baseOffset), $"0x{baseOffset:X} is outside the ROM.");

        int count = (int)U32(rom.Data, baseOffset);
        if (count <= 0 || baseOffset + HeaderSize + (long)count * RecordSize > rom.Length)
            throw new InvalidDataException($"Implausible file count {count} at 0x{baseOffset:X}.");

        // The header carries an ASCII build stamp after the count and a reserved word.
        string stamp = Encoding.ASCII
            .GetString(rom.Data, baseOffset + 8, HeaderSize - 8)
            .Split('\n')[0]
            .Trim('\0', ' ');

        var entries = new List<AssetEntry>(count);
        int nullSlots = 0;

        for (int i = 0; i < count; i++)
        {
            int record = baseOffset + HeaderSize + i * RecordSize;
            uint offset = U32(rom.Data, record);
            int storedSize = (int)U32(rom.Data, record + 4);

            if (offset == NullOffset) { nullSlots++; continue; }

            int romOffset = baseOffset + (int)offset;
            int padded = storedSize + (storedSize & 1);
            int trailer = romOffset + padded;
            if (trailer + TrailerSize > rom.Length) { nullSlots++; continue; }

            uint tag = U32(rom.Data, trailer);
            int declaredSize = (int)U32(rom.Data, trailer + 4);

            entries.Add(new AssetEntry(i, (int)offset, storedSize, romOffset, (AssetKind)(tag >> 16), declaredSize));
        }

        return new AssetDirectory(baseOffset, count, stamp, entries, nullSlots);
    }

    /// <summary>
    /// Optional source of replacement bytes, keyed by asset id, consulted before the ROM.
    /// </summary>
    public Func<int, byte[]?>? OverrideProvider { get; set; }

    public bool TryGetData(RomFile rom, AssetEntry entry, out byte[] data)
    {
        data = Array.Empty<byte>();

        if (OverrideProvider?.Invoke(entry.Index) is { } replacement)
        {
            data = replacement;
            return true;
        }

        return TryGetCartData(rom, entry, out data);
    }

    /// <summary>The asset as the cart holds it, ignoring <see cref="OverrideProvider"/>.</summary>
    public bool TryGetCartData(RomFile rom, AssetEntry entry, out byte[] data)
    {
        data = Array.Empty<byte>();

        switch (entry.Kind)
        {
            case AssetKind.Stored:
                if (entry.RomOffset + entry.StoredSize > rom.Length) return false;
                data = rom.Slice(entry.RomOffset, entry.StoredSize).ToArray();
                return true;

            case AssetKind.Deflate:
            {
                int start = entry.RomOffset + DeflateBlockHeaderSize;
                int available = Math.Min(entry.StoredSize + 64, rom.Length - start);
                if (start < 0 || available <= 0) return false;
                if (!RawDeflate.TryDecompress(rom.Slice(start, available), out var output, out _)) return false;
                if (output.Length != entry.DecompressedSize) return false;
                data = output;
                return true;
            }

            case AssetKind.Rle:
            case AssetKind.Rle16:
            {
                if (entry.RomOffset + entry.StoredSize > rom.Length) return false;
                var stored = rom.Slice(entry.RomOffset, entry.StoredSize);
                var output = entry.Kind == AssetKind.Rle
                    ? PackBits.DecodeBytes(stored)
                    : PackBits.DecodeUnits(stored);
                if (output.Length != entry.DecompressedSize) return false;
                data = output;
                return true;
            }

            default:
                return false;
        }
    }

    public IEnumerable<IGrouping<AssetKind, AssetEntry>> ByKind() => Entries.GroupBy(e => e.Kind);

    /// <summary>Finds the entry whose payload starts at a given ROM offset, if any.</summary>
    public AssetEntry? FindByRomOffset(int romOffset)
    {
        foreach (var entry in Entries)
            if (entry.RomOffset == romOffset) return entry;
        return null;
    }
}
