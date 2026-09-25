using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>The inventory item names -- "Green Herb", "F.</summary>
public static class ItemNames
{
    /// <summary>Where the names begin. Rev 1 addresses; <see cref="ModelTextureTable.Overlay.At"/> moves them.</summary>
    public const uint NamesAddress = 0x800FACB0;

    /// <summary>The offset table, which also marks the end of the names.</summary>
    public const uint TableAddress = 0x800FB3F8;

    /// <summary>Where the table ends, and the message text after it begins.</summary>
    public const uint TableEndAddress = 0x800FB530;

    public const int Count = (int)((TableEndAddress - TableAddress) / 2);

    /// <summary>Bytes available for the names, separators and all.</summary>
    public const int Capacity = (int)(TableAddress - NamesAddress);

    /// <summary>
    /// Where a language's names and table are. English is the Rev 1 pair, moved for the build;
    /// The alternate is the second pair: French in Europe, Japanese in Japan.
    /// </summary>
    private static (uint Names, uint Table) Where(ModelTextureTable.Overlay overlay, bool alternate)
    {
        if (!alternate) return (overlay.At(NamesAddress), overlay.At(TableAddress));
        var alt = overlay.Layout?.AlternateInventoryText
                  ?? throw new InvalidOperationException("This build has no second language.");
        return (alt.NamesAddress, alt.NamesTable);
    }

    /// <summary>Bytes available for one language's names in a build.</summary>
    public static int CapacityFor(Re2Layout layout, bool alternate)
        => alternate && layout.AlternateInventoryText is { } alt
            ? (int)(alt.NamesTable - alt.NamesAddress)
            : Capacity;

    /// <summary>The glyph set one language of a build is written in.</summary>
    public static ItemCharset CharsetOf(Re2Layout? layout, bool alternate)
        => alternate ? layout?.AlternateInventoryText?.Charset ?? ItemCharset.English : ItemCharset.English;

    public static List<string> Read(RomFile rom, bool alternate = false)
        => Read(ModelTextureTable.LoadMainOverlay(rom), alternate);

    public static List<string> Read(ModelTextureTable.Overlay overlay, bool alternate = false)
    {
        var names = new List<string>(Count);

        var (names0, table) = Where(overlay, alternate);

        for (int i = 0; i < Count; i++)
        {
            int offset = overlay.U16(table + (uint)i * 2);
            int at = (int)(names0 - overlay.BaseAddress) + offset;

            int end = at;
            while (end < overlay.Data.Length && overlay.Data[end] != ItemText.NameSeparator) end++;

            names.Add(ItemText.Decode(overlay.Data.AsSpan(at, end - at), CharsetOf(overlay.Layout, alternate)));
        }

        return names;
    }

    /// <summary>
    /// How many of the available bytes a set of names would occupy, or -1 if one of them cannot be
    /// written.
    /// </summary>
    public static int Measure(IReadOnlyList<string> names, ItemCharset charset = ItemCharset.English)
        => Layout(names, charset, out var bytes, out _, out _) ? bytes.Count : -1;

    /// <summary>Lays the names out and builds the table that indexes them.</summary>
    public static bool TryBuild(IReadOnlyList<string> names, out byte[] block, out byte[] table,
                                out string error)
        => TryBuild(names, Capacity, ItemCharset.English, out block, out table, out error);

    public static bool TryBuild(IReadOnlyList<string> names, int capacity, ItemCharset charset,
                                out byte[] block, out byte[] table, out string error)
    {
        block = Array.Empty<byte>();
        table = Array.Empty<byte>();

        if (!Layout(names, charset, out var bytes, out var offsets, out error)) return false;

        if (bytes.Count > capacity)
        {
            error = $"The names need {bytes.Count:N0} bytes but only {capacity:N0} are available, " +
                    $"{bytes.Count - capacity:N0} too many. Shorten some of them.";
            return false;
        }

        // The rest of the block is cleared rather than left holding whatever was there before, so
        // a shorter set of names cannot leave the tail of a longer one lying around.
        var padded = new byte[capacity];
        bytes.CopyTo(padded);

        var indexed = new byte[Count * 2];
        for (int i = 0; i < Count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(indexed.AsSpan(i * 2, 2), (ushort)offsets[i]);

        block = padded;
        table = indexed;
        return true;
    }

    /// <summary>
    /// Runs the names together with their separators and records where each one landed, without yet
    /// caring whether the result fits.
    /// </summary>
    private static bool Layout(IReadOnlyList<string> names, ItemCharset charset, out List<byte> bytes,
                               out int[] offsets, out string error)
    {
        bytes = new List<byte> { ItemText.NameSeparator };
        offsets = new int[Count];
        error = "";

        if (names.Count != Count)
        {
            error = $"There must be exactly {Count} names; {names.Count} were given.";
            return false;
        }

        var already = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < Count; i++)
        {
            string name = names[i];

            // The empty name is the separator the block already opens with.
            if (name.Length == 0) { offsets[i] = 0; continue; }

            if (already.TryGetValue(name, out int shared)) { offsets[i] = shared; continue; }

            if (!ItemText.TryEncode(name, out var codes, out error, charset)) return false;

            offsets[i] = bytes.Count;
            bytes.AddRange(codes);
            bytes.Add(ItemText.NameSeparator);
            already[name] = offsets[i];
        }

        return true;
    }

    /// <summary>Writes a set of names into a decompressed overlay image in place.</summary>
    public static bool TryWrite(ModelTextureTable.Overlay overlay, IReadOnlyList<string> names,
                                out string error, bool alternate = false)
    {
        int capacity = CapacityFor(overlay.Layout ?? Re2Layout.UsaRev1, alternate);
        if (!TryBuild(names, capacity, CharsetOf(overlay.Layout, alternate), out var block, out var table, out error)) return false;

        var (at, tableAt) = Where(overlay, alternate);
        block.CopyTo(overlay.Data, (int)(at - overlay.BaseAddress));
        table.CopyTo(overlay.Data, (int)(tableAt - overlay.BaseAddress));
        return true;
    }
}
