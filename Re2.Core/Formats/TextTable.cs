using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Re2.Core.Assets;
using Re2.Core.Rom;

namespace Re2.Core.Formats;

/// <summary>One editable string in the ROM, together with the asset it came from.</summary>
public sealed record TextEntry(int AssetId, AssetKind Kind, string Text)
{
    public int Lines => Text.Count(c => c == '\n') + 1;
    public override string ToString() => $"{AssetId}: {Text.Replace("\r\n", " / ")}";
}

/// <summary>The game's readable text -- the files, memos and diaries the player picks up.</summary>
public static class TextTable
{
    /// <summary>Shortest run of bytes worth treating as an editable string.</summary>
    public const int MinimumLength = 2;

    /// <summary>Fraction of bytes that must be printable ASCII or CR/LF.</summary>
    public const double PrintableThreshold = 0.98;

    /// <summary>Decides whether a blob is text.</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumLength) return false;

        int printable = 0, letters = 0, spacing = 0;
        foreach (byte b in data)
        {
            if (b is >= 32 and < 127) printable++;
            else if (b is (byte)'\r' or (byte)'\n') { printable++; spacing++; }
            else return false;                       // any other control byte disqualifies it

            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z') letters++;
            else if (b == ' ') spacing++;
        }

        if (printable < data.Length * PrintableThreshold) return false;
        return letters >= data.Length * 0.35 && spacing > 0;
    }

    /// <summary>Every text asset in the ROM, in id order.</summary>
    public static List<TextEntry> Read(RomFile rom, AssetDirectory directory)
    {
        var found = new List<TextEntry>();
        var seen = new HashSet<int>();

        foreach (var entry in directory.Entries.OrderBy(e => e.Index))
        {
            if (!seen.Add(entry.Index)) continue;
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (!LooksLikeText(data)) continue;

            found.Add(new TextEntry(entry.Index, entry.Kind, Decode(data)));
        }

        return found;
    }

    public static string Decode(ReadOnlySpan<byte> data) => Encoding.ASCII.GetString(data);

    /// <summary>Turns an edited string back into bytes.</summary>
    public static byte[] Encode(string text)
    {
        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

        // The renderer has no glyph for anything outside ASCII; refuse rather than emit mojibake.
        foreach (char c in normalised)
            if (c > 126 || (c < 32 && c is not ('\r' or '\n')))
                throw new ArgumentException(
                    $"'{c}' (U+{(int)c:X4}) is not ASCII; the game's font cannot render it.");

        return Encoding.ASCII.GetBytes(normalised);
    }
}
