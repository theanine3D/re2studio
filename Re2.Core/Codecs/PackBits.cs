using System;
using System.Collections.Generic;
using System.IO;

namespace Re2.Core.Codecs;

/// <summary>
/// The two run-length codecs the asset container uses, which are the same scheme at two different unit
/// sizes.
/// </summary>
public static class PackBits
{
    public const int MaxLiteralBytes = 0x7F;
    public const int MaxRunBytes = 0x80;
    public const int MaxLiteralUnits = 0x7FFF;
    public const int MaxRunUnits = 0x7FFF;

    // ---- type 2: byte units ----------------------------------------------

    public static byte[] DecodeBytes(ReadOnlySpan<byte> source)
    {
        var output = new List<byte>(source.Length * 2);
        int at = 0;

        while (at < source.Length)
        {
            int control = source[at++];
            if (control == 0) break;

            if (control < 0x80)
            {
                int count = Math.Min(control, source.Length - at);
                for (int i = 0; i < count; i++) output.Add(source[at + i]);
                at += count;
            }
            else
            {
                if (at >= source.Length) break;
                byte value = source[at++];
                for (int i = 0; i < 0x100 - control; i++) output.Add(value);
            }
        }

        return output.ToArray();
    }

    public static byte[] EncodeBytes(ReadOnlySpan<byte> source)
    {
        var output = new List<byte>(source.Length + source.Length / 8 + 2);
        int at = 0;

        while (at < source.Length)
        {
            int run = RunLength(source, at, 1);

            // A run only pays for itself at 3 or more; shorter ones ride along in a literal.
            if (run >= 3)
            {
                int take = Math.Min(run, MaxRunBytes);
                output.Add((byte)(0x100 - take));
                output.Add(source[at]);
                at += take;
                continue;
            }

            int literal = LiteralLength(source, at, 1, MaxLiteralBytes);
            output.Add((byte)literal);
            for (int i = 0; i < literal; i++) output.Add(source[at + i]);
            at += literal;
        }

        output.Add(0);
        return output.ToArray();
    }

    // ---- type 4: 16-bit units --------------------------------------------

    public static byte[] DecodeUnits(ReadOnlySpan<byte> source)
    {
        var output = new List<byte>(source.Length * 2);
        int at = 0;

        while (at + 2 <= source.Length)
        {
            int control = (short)((source[at] << 8) | source[at + 1]);
            at += 2;
            if (control == 0) break;

            if (control > 0)
            {
                int count = Math.Min(control * 2, source.Length - at);
                for (int i = 0; i < count; i++) output.Add(source[at + i]);
                at += count;
            }
            else
            {
                if (at + 2 > source.Length) break;
                byte hi = source[at], lo = source[at + 1];
                at += 2;
                for (int i = 0; i < -control; i++) { output.Add(hi); output.Add(lo); }
            }
        }

        return output.ToArray();
    }

    public static byte[] EncodeUnits(ReadOnlySpan<byte> source)
    {
        if ((source.Length & 1) != 0)
            throw new InvalidDataException("A 16-bit RLE payload must have an even length.");

        var output = new List<byte>(source.Length + source.Length / 8 + 2);
        int at = 0;

        while (at < source.Length)
        {
            int run = RunLength(source, at, 2);

            if (run >= 2)
            {
                int take = Math.Min(run, MaxRunUnits);
                output.Add((byte)((-take >> 8) & 0xFF));
                output.Add((byte)(-take & 0xFF));
                output.Add(source[at]);
                output.Add(source[at + 1]);
                at += take * 2;
                continue;
            }

            int literal = LiteralLength(source, at, 2, MaxLiteralUnits);
            output.Add((byte)((literal >> 8) & 0xFF));
            output.Add((byte)(literal & 0xFF));
            for (int i = 0; i < literal * 2; i++) output.Add(source[at + i]);
            at += literal * 2;
        }

        output.Add(0);
        output.Add(0);
        return output.ToArray();
    }

    // ---- shared ----------------------------------------------------------

    /// <summary>How many consecutive units at <paramref name="at"/> repeat the first one.</summary>
    private static int RunLength(ReadOnlySpan<byte> source, int at, int unit)
    {
        int count = 1;
        for (int p = at + unit; p + unit <= source.Length; p += unit)
        {
            bool same = true;
            for (int b = 0; b < unit; b++) if (source[p + b] != source[at + b]) { same = false; break; }
            if (!same) break;
            count++;
        }
        return count;
    }

    /// <summary>
    /// How many units to emit as a literal: stop where a run worth encoding begins, so the literal
    /// does not swallow a repeat that would have been cheaper on its own.
    /// </summary>
    private static int LiteralLength(ReadOnlySpan<byte> source, int at, int unit, int max)
    {
        int total = (source.Length - at) / unit;
        int threshold = unit == 1 ? 3 : 2;
        int count = 0;

        while (count < total && count < max)
        {
            if (count > 0 && RunLength(source, at + count * unit, unit) >= threshold) break;
            count++;
        }

        return Math.Max(1, count);
    }
}
