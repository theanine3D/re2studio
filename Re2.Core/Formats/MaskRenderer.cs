using System;

namespace Re2.Core.Formats;

/// <summary>
/// Turns a <see cref="MaskFile"/> into an image: the layer that draws in front of the player.
/// </summary>
public static class MaskRenderer
{
    /// <summary>The foreground layer as RGBA, the size of the mask's screen.</summary>
    public static byte[] Compose(MaskFile mask, ReadOnlySpan<byte> background, int backgroundWidth, int backgroundHeight)
    {
        var layer = new byte[mask.Width * mask.Height * 4];

        foreach (var piece in mask.Pieces)
        {
            for (int row = 0; row < piece.Height; row++)
            {
                int y = piece.Y + row;
                if (y < 0 || y >= mask.Height) continue;

                for (int column = 0; column < piece.Width; column++)
                {
                    if (!piece.Covered[row * piece.Width + column]) continue;

                    int x = piece.X + column;
                    if (x < 0 || x >= mask.Width) continue;

                    int to = (y * mask.Width + x) * 4;

                    if (piece.HasOwnPixels && mask.Palette.Count == MaskFile.PaletteEntries)
                    {
                        byte index = piece.Pixels[row * piece.Width + column];
                        var (r, g, b, a) = FromPalette(mask.Palette[index]);
                        if (a == 0) continue;

                        layer[to] = r; layer[to + 1] = g; layer[to + 2] = b; layer[to + 3] = 255;
                    }
                    else
                    {
                        // No colour of its own: the pixel is the background's, drawn again in front.
                        if (x >= backgroundWidth || y >= backgroundHeight || background.IsEmpty) continue;

                        int from = (y * backgroundWidth + x) * 4;
                        if (from + 3 >= background.Length) continue;

                        layer[to] = background[from];
                        layer[to + 1] = background[from + 1];
                        layer[to + 2] = background[from + 2];
                        layer[to + 3] = 255;
                    }
                }
            }
        }

        return layer;
    }

    /// <summary>The background with its foreground pixels tinted, so they can be seen at all.</summary>
    public static byte[] Highlight(MaskFile mask, ReadOnlySpan<byte> background,
                                   int backgroundWidth, int backgroundHeight,
                                   byte tintR = 255, byte tintG = 210, byte tintB = 40, float strength = 0.45f)
    {
        var result = new byte[mask.Width * mask.Height * 4];

        // Start from the background, so untouched areas look normal.
        for (int y = 0; y < mask.Height; y++)
        {
            for (int x = 0; x < mask.Width; x++)
            {
                int to = (y * mask.Width + x) * 4;
                result[to + 3] = 255;

                if (x >= backgroundWidth || y >= backgroundHeight || background.IsEmpty) continue;

                int from = (y * backgroundWidth + x) * 4;
                if (from + 3 >= background.Length) continue;

                result[to] = background[from];
                result[to + 1] = background[from + 1];
                result[to + 2] = background[from + 2];
            }
        }

        var layer = Compose(mask, background, backgroundWidth, backgroundHeight);

        for (int i = 0; i < mask.Width * mask.Height; i++)
        {
            if (layer[i * 4 + 3] == 0) continue;

            int at = i * 4;
            result[at] = Blend(layer[at], tintR, strength);
            result[at + 1] = Blend(layer[at + 1], tintG, strength);
            result[at + 2] = Blend(layer[at + 2], tintB, strength);
        }

        return result;
    }

    private static byte Blend(byte value, byte towards, float strength)
        => (byte)Math.Clamp(value + (towards - value) * strength, 0, 255);

    /// <summary>A palette entry, which is 5 bits per channel and one of alpha, as the console uses.</summary>
    private static (byte R, byte G, byte B, byte A) FromPalette(ushort colour)
    {
        int r = (colour >> 11) & 0x1F;
        int g = (colour >> 6) & 0x1F;
        int b = (colour >> 1) & 0x1F;

        static byte Expand(int five) => (byte)((five << 3) | (five >> 2));

        return (Expand(r), Expand(g), Expand(b), (byte)(colour & 1));
    }
}
