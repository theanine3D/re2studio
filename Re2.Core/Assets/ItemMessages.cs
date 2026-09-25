using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>
/// The examine text and inventory prompts -- "It uses 9mm parabellum rounds", "You can't carry any more
/// items", "Will you mix the herb?" -- which live in overlay 1 beside the item names.
/// </summary>
public static class ItemMessages
{
    /// <summary>Where the messages begin. Rev 1 addresses; <see cref="ModelTextureTable.Overlay.At"/> moves them.</summary>
    public const uint TextAddress = 0x800FC900;

    /// <summary>The offset table, which also marks the end of the text.</summary>
    public const uint TableAddress = 0x800FDFA0;

    /// <summary>Where the table ends. Beyond it are the short option words (Yes, No, Left, Right).</summary>
    public const uint TableEndAddress = 0x800FE088;

    public const int Count = (int)((TableEndAddress - TableAddress) / 2);

    /// <summary>Bytes available for the messages altogether.</summary>
    public const int Capacity = (int)(TableAddress - TextAddress);

    /// <summary>How far an item's id is from the record describing it.</summary>
    public const int ItemOffset = 16;

    /// <summary>The record describing an item, or -1 when that item has no description.</summary>
    public static int RecordForItem(int itemId)
    {
        int record = itemId + ItemOffset;
        return itemId >= 1 && record < Count ? record : -1;
    }

    /// <summary>The item a record describes, or -1 when the record is a prompt rather than a description.</summary>
    public static int ItemForRecord(int record)
    {
        int itemId = record - ItemOffset;
        return itemId >= 1 && itemId < ItemNames.Count ? itemId : -1;
    }

    /// <summary>Where a language's text and table are; the alternate is Europe's French or Japan's Japanese.</summary>
    private static (uint Text, uint Table) Where(ModelTextureTable.Overlay overlay, bool alternate)
    {
        if (!alternate) return (overlay.At(TextAddress), overlay.At(TableAddress));
        var alt = overlay.Layout?.AlternateInventoryText
                  ?? throw new InvalidOperationException("This build has no second language.");
        return (alt.MessagesText, alt.MessagesTable);
    }

    /// <summary>Bytes available for one language's messages in a build.</summary>
    public static int CapacityFor(Re2Layout layout, bool alternate)
        => alternate && layout.AlternateInventoryText is { } alt
            ? (int)(alt.MessagesTable - alt.MessagesText)
            : Capacity;

    /// <summary>The glyph set one language of a build is written in.</summary>
    public static ItemCharset CharsetOf(Re2Layout? layout, bool alternate)
        => alternate ? layout?.AlternateInventoryText?.Charset ?? ItemCharset.English : ItemCharset.English;

    public static List<string> Read(RomFile rom, bool alternate = false)
        => Read(ModelTextureTable.LoadMainOverlay(rom), alternate);

    public static List<string> Read(ModelTextureTable.Overlay overlay, bool alternate = false)
    {
        var (textAt, tableAt) = Where(overlay, alternate);
        var offsets = ReadOffsets(overlay, tableAt);
        var text = new List<string>(Count);

        int start = (int)(textAt - overlay.BaseAddress);

        // The block is longer than the text in it, and the slack is zeroes.
        int used = CapacityFor(overlay.Layout ?? Re2Layout.UsaRev1, alternate);
        while (used > 0 && overlay.Data[start + used - 1] == 0) used--;

        for (int i = 0; i < Count; i++)
        {
            int from = offsets[i];
            int to = EndOf(offsets, from, used);
            text.Add(ItemText.Decode(overlay.Data.AsSpan(start + from, Math.Max(0, to - from)), CharsetOf(overlay.Layout, alternate)));
        }

        return text;
    }

    private static int[] ReadOffsets(ModelTextureTable.Overlay overlay, uint table)
    {
        var offsets = new int[Count];
        for (int i = 0; i < Count; i++) offsets[i] = overlay.U16(table + (uint)i * 2);
        return offsets;
    }

    /// <summary>Where a record ends: at the next offset any record starts at.</summary>
    private static int EndOf(IReadOnlyList<int> offsets, int from, int used)
    {
        int end = used;
        foreach (int other in offsets) if (other > from && other < end) end = other;
        return end;
    }

    /// <summary>How many of the available bytes these messages would occupy, or -1 if one cannot be written.</summary>
    public static int Measure(IReadOnlyList<string> messages, ItemCharset charset = ItemCharset.English)
        => Layout(messages, charset, out var bytes, out _, out _) ? bytes.Count : -1;

    /// <summary>Lays the messages out and builds the table that indexes them.</summary>
    public static bool TryBuild(IReadOnlyList<string> messages, out byte[] block, out byte[] table,
                                out string error)
        => TryBuild(messages, Capacity, ItemCharset.English, out block, out table, out error);

    public static bool TryBuild(IReadOnlyList<string> messages, int capacity, ItemCharset charset,
                                out byte[] block, out byte[] table, out string error)
    {
        block = Array.Empty<byte>();
        table = Array.Empty<byte>();

        if (!Layout(messages, charset, out var bytes, out var offsets, out error)) return false;

        if (bytes.Count > capacity)
        {
            error = $"The item text needs {bytes.Count:N0} bytes but only {capacity:N0} are " +
                    $"available, {bytes.Count - capacity:N0} too many. Shorten something.";
            return false;
        }

        var padded = new byte[capacity];
        bytes.CopyTo(padded);

        var indexed = new byte[Count * 2];
        for (int i = 0; i < Count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(indexed.AsSpan(i * 2, 2), (ushort)offsets[i]);

        block = padded;
        table = indexed;
        return true;
    }

    /// <summary>Runs the records together and records where each one landed.</summary>
    private static bool Layout(IReadOnlyList<string> messages, ItemCharset charset, out List<byte> bytes,
                               out int[] offsets, out string error)
    {
        bytes = new List<byte>();
        offsets = new int[Count];
        error = "";

        if (messages.Count != Count)
        {
            error = $"There must be exactly {Count} item text records; {messages.Count} were given.";
            return false;
        }

        var already = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < Count; i++)
        {
            if (already.TryGetValue(messages[i], out int shared)) { offsets[i] = shared; continue; }

            if (!ItemText.TryEncode(messages[i], out var codes, out error, charset)) return false;

            offsets[i] = bytes.Count;
            bytes.AddRange(codes);
            already[messages[i]] = offsets[i];
        }

        return true;
    }

    /// <summary>Writes a set of messages into a decompressed overlay image in place.</summary>
    public static bool TryWrite(ModelTextureTable.Overlay overlay, IReadOnlyList<string> messages,
                                out string error, bool alternate = false)
    {
        int capacity = CapacityFor(overlay.Layout ?? Re2Layout.UsaRev1, alternate);
        if (!TryBuild(messages, capacity, CharsetOf(overlay.Layout, alternate), out var block, out var table, out error)) return false;

        var (textAt, tableAt) = Where(overlay, alternate);
        block.CopyTo(overlay.Data, (int)(textAt - overlay.BaseAddress));
        table.CopyTo(overlay.Data, (int)(tableAt - overlay.BaseAddress));
        return true;
    }
}
