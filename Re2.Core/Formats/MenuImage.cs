using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Re2.Core.Formats;

/// <summary>
/// The screens the game draws outside the 3D world: the inventory, the map, the file viewer.
/// </summary>
public sealed class MenuImage
{
    public int Width { get; }
    public int Height { get; }
    public int Colours { get; }

    /// <summary>Palette-index-per-pixel, row-major.</summary>
    public byte[] Indices { get; }

    /// <summary>Each palette as stored, RGBA5551.</summary>
    public IReadOnlyList<ushort[]> Palettes { get; }

    public int Kind { get; }

    public const int HeaderSize = 20;

    /// <summary>
    /// A reasonable palette to open on: the one the character portraits use, for a screen that has
    /// several.
    /// </summary>
    public int DisplayPalette => Palettes.Count > 2 ? 2 : 0;

    private MenuImage(int kind, int width, int height, int colours, byte[] indices, IReadOnlyList<ushort[]> palettes)
    {
        Kind = kind;
        Width = width;
        Height = height;
        Colours = colours;
        Indices = indices;
        Palettes = palettes;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out MenuImage? image)
    {
        image = null;
        if (data.Length < HeaderSize) return false;

        int kind = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        int colours = (int)BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        int palettes = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);

        // No magic to check, so the shape has to carry the whole burden: the size must come out
        // exactly, which is what stops this from claiming every blob that starts with a small number.
        if (kind != 1) return false;
        if (width is < 8 or > 1024 || height is < 8 or > 1024) return false;
        if (colours != 256) return false;
        if (palettes is < 1 or > 16) return false;

        int expected = HeaderSize + width * height + palettes * colours * 2;
        if (data.Length != expected) return false;

        var indices = data.Slice(HeaderSize, width * height).ToArray();

        var sets = new List<ushort[]>(palettes);
        for (int p = 0; p < palettes; p++)
        {
            var set = new ushort[colours];
            int at = HeaderSize + width * height + p * colours * 2;
            for (int i = 0; i < colours; i++)
                set[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(at + i * 2)..]);

            sets.Add(set);
        }

        image = new MenuImage(kind, width, height, colours, indices, sets);
        return true;
    }

    /// <summary>RGBA for one palette, or <see cref="DisplayPalette"/> when none is named.</summary>
    public byte[] ToRgba(int palette = -1)
    {
        if (palette < 0) palette = DisplayPalette;
        if (palette >= Palettes.Count) palette = 0;

        var set = Palettes[palette];
        var rgba = new byte[Width * Height * 4];

        for (int i = 0; i < Indices.Length; i++)
        {
            ushort colour = set[Indices[i]];

            static byte Expand(int five) => (byte)((five << 3) | (five >> 2));

            rgba[i * 4] = Expand((colour >> 11) & 0x1F);
            rgba[i * 4 + 1] = Expand((colour >> 6) & 0x1F);
            rgba[i * 4 + 2] = Expand((colour >> 1) & 0x1F);
            rgba[i * 4 + 3] = (byte)((colour & 1) != 0 ? 255 : 0);
        }

        return rgba;
    }

    /// <summary>One palette as RGBA, in the order the game reads it -- entry 0 first.</summary>
    public byte[] PaletteToRgba(int palette)
    {
        if (palette < 0) palette = DisplayPalette;
        if (palette >= Palettes.Count) palette = 0;

        var set = Palettes[palette];
        var rgba = new byte[set.Length * 4];

        for (int i = 0; i < set.Length; i++)
        {
            ushort colour = set[i];

            static byte Expand(int five) => (byte)((five << 3) | (five >> 2));

            rgba[i * 4] = Expand((colour >> 11) & 0x1F);
            rgba[i * 4 + 1] = Expand((colour >> 6) & 0x1F);
            rgba[i * 4 + 2] = Expand((colour >> 1) & 0x1F);
            rgba[i * 4 + 3] = (byte)((colour & 1) != 0 ? 255 : 0);
        }

        return rgba;
    }

    /// <summary>The same image with one palette replaced and the pixels untouched.</summary>
    public MenuImage WithPalette(int palette, ReadOnlySpan<byte> rgba)
    {
        if (palette < 0 || palette >= Palettes.Count)
            throw new InvalidDataException($"This screen has palettes 0-{Palettes.Count - 1}, not {palette}.");

        if (rgba.Length < Colours * 4)
            throw new InvalidDataException(
                $"A palette is {Colours} colours, so {Colours * 4:N0} bytes of RGBA; got {rgba.Length:N0}.");

        var replaced = new ushort[Colours];
        for (int i = 0; i < Colours; i++)
        {
            int r = rgba[i * 4] >> 3, g = rgba[i * 4 + 1] >> 3, b = rgba[i * 4 + 2] >> 3;
            int a = rgba[i * 4 + 3] >= 128 ? 1 : 0;

            replaced[i] = (ushort)((r << 11) | (g << 6) | (b << 1) | a);
        }

        var sets = new List<ushort[]>(Palettes);
        sets[palette] = replaced;

        return new MenuImage(Kind, Width, Height, Colours, Indices, sets);
    }

    /// <summary>Writes the container back, byte for byte given untouched content.</summary>
    public byte[] Write()
    {
        var output = new byte[HeaderSize + Width * Height + Palettes.Count * Colours * 2];

        BinaryPrimitives.WriteUInt32BigEndian(output, (uint)Kind);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)Width);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8), (uint)Height);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12), (uint)Colours);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16), (uint)Palettes.Count);

        Indices.CopyTo(output, HeaderSize);

        for (int p = 0; p < Palettes.Count; p++)
        {
            int at = HeaderSize + Width * Height + p * Colours * 2;
            for (int i = 0; i < Colours; i++)
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(at + i * 2), Palettes[p][i]);
        }

        return output;
    }

    /// <summary>The same image with new pixels, matched into the palettes it already has.</summary>
    public MenuImage WithPixels(ReadOnlySpan<byte> rgba, int width, int height, int palette = -1)
    {
        if (width != Width || height != Height)
            throw new InvalidDataException(
                $"The replacement is {width}x{height} but the image is {Width}x{Height}. " +
                "The screen addresses this by pixel coordinate, so the dimensions are fixed.");

        if (rgba.Length < width * height * 4)
            throw new InvalidDataException($"Expected {width * height * 4:N0} bytes of RGBA, got {rgba.Length:N0}.");

        if (palette < 0) palette = DisplayPalette;
        if (palette >= Palettes.Count) palette = 0;

        var set = Palettes[palette];
        var indices = new byte[Width * Height];

        for (int i = 0; i < indices.Length; i++)
        {
            // Compared at the 5 bits the palette actually stores.
            int r = rgba[i * 4] >> 3, g = rgba[i * 4 + 1] >> 3, b = rgba[i * 4 + 2] >> 3, a = rgba[i * 4 + 3];

            int best = 0, bestScore = int.MaxValue;
            for (int c = 0; c < set.Length; c++)
            {
                ushort colour = set[c];
                bool opaque = (colour & 1) != 0;

                // Transparency is a property of the palette entry, so a transparent pixel can only
                // be matched by an entry that is itself transparent.
                if (opaque != a >= 128) continue;

                int dr = ((colour >> 11) & 0x1F) - r;
                int dg = ((colour >> 6) & 0x1F) - g;
                int db = ((colour >> 1) & 0x1F) - b;

                int score = dr * dr + dg * dg + db * db;
                if (score >= bestScore) continue;

                best = c;
                bestScore = score;
                if (score == 0) break;
            }

            indices[i] = (byte)best;
        }

        return new MenuImage(Kind, Width, Height, Colours, indices, Palettes);
    }
}
