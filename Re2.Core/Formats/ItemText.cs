using System;
using System.Collections.Generic;
using System.Text;

namespace Re2.Core.Formats;

/// <summary>The character codes the game's menu and inventory text is written in.</summary>
public static class ItemText
{
    /// <summary>Separates one name from the next in the item name list.</summary>
    public const byte NameSeparator = 0xF7;

    /// <summary>Ends a line within a message; read and written as a newline.</summary>
    public const byte LineBreak = 0xFC;

    /// <summary>Starts a new page of an examine message.</summary>
    public const byte PageBreak = 0xFD;

    /// <summary>Ends a message.</summary>
    public const byte MessageEnd = 0xFE;

    private static readonly Dictionary<byte, char> ToChar = new()
    {
        [0x00] = ' ',
        [0x01] = '.',
        [0x09] = '“',
        [0x0A] = '”',
        [0x18] = ',',
        [0x1A] = '!',
        [0x1B] = '?',
        [0x38] = '/',
        [0x3A] = '\'',
        [0x3B] = '-',

        // Messages wrap by hand rather than by measuring, so a line break is a character like any
        // other. Names never contain one, so carrying it here costs them nothing.
        [LineBreak] = '\n',
    };

    private static readonly Dictionary<char, byte> ToCode = Build();

    private static Dictionary<char, byte> Build()
    {
        var map = new Dictionary<char, byte>();
        foreach (var (code, character) in ToChar) map[character] = code;

        for (int i = 0; i < 10; i++) map[(char)('0' + i)] = (byte)(0x0C + i);
        for (int i = 0; i < 26; i++) map[(char)('A' + i)] = (byte)(0x1D + i);
        for (int i = 0; i < 26; i++) map[(char)('a' + i)] = (byte)(0x3D + i);

        // Typing a straight quote should work as well as pasting a curly one.
        map['"'] = 0x09;

        return map;
    }

    /// <summary>Turns the game's codes into text, escaping anything with no known meaning.</summary>
    public static string Decode(ReadOnlySpan<byte> codes)
    {
        var text = new StringBuilder(codes.Length);

        foreach (byte code in codes)
        {
            if (code is >= 0x0C and <= 0x15) text.Append((char)('0' + code - 0x0C));
            else if (code is >= 0x1D and <= 0x36) text.Append((char)('A' + code - 0x1D));
            else if (code is >= 0x3D and <= 0x56) text.Append((char)('a' + code - 0x3D));
            else if (ToChar.TryGetValue(code, out char character)) text.Append(character);
            else text.Append('<').Append(code.ToString("X2")).Append('>');
        }

        return text.ToString();
    }

    /// <summary>
    /// Turns text back into the game's codes, reading <c>&lt;XX&gt;</c> as the byte it names.
    /// </summary>
    public static bool TryEncode(string text, out byte[] codes, out string error)
    {
        var output = new List<byte>(text.Length);
        codes = Array.Empty<byte>();
        error = "";

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '<')
            {
                int close = text.IndexOf('>', i);
                if (close != i + 3 ||
                    !byte.TryParse(text.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                                   null, out byte raw))
                {
                    error = $"{Quote(text)}: an escape must be exactly <XX> with two hex digits.";
                    return false;
                }

                output.Add(raw);
                i = close;
                continue;
            }

            if (c is '‘' or '’') c = '\'';          // curly apostrophes, from pasted text
            if (c is '–' or '—') c = '-';

            if (!ToCode.TryGetValue(c, out byte code))
            {
                error = $"{Quote(text)}: the game has no character for '{c}'.";
                return false;
            }

            output.Add(code);
        }

        codes = output.ToArray();
        return true;
    }

    /// <summary>Enough of a string to recognise it by, for a message that may be several lines.</summary>
    private static string Quote(string text)
    {
        string flat = text.Replace('\n', ' ').Trim();
        return "\"" + (flat.Length > 40 ? flat[..40] + "..." : flat) + "\"";
    }

    /// <summary>How many bytes this text will take, or -1 when it cannot be written at all.</summary>
    public static int MeasureOrMinusOne(string text)
        => TryEncode(text, out var codes, out _) ? codes.Length : -1;
}
