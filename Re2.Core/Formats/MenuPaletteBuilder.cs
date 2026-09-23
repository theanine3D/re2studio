using System;
using System.Collections.Generic;
using System.Linq;

namespace Re2.Core.Formats;

/// <summary>What building a palette for a picture did.</summary>
public sealed record MenuPaletteResult(
    byte[] PaletteRgba,
    int DistinctColours,
    int PaletteEntries,
    bool Merged,
    bool HasTransparency);

/// <summary>
/// Builds a fresh palette for a picture, for when the picture's colours are not in the screen's
/// existing palette at all.
/// </summary>
public static class MenuPaletteBuilder
{
    private readonly record struct Colour(int R, int G, int B, int Count);

    /// <summary>A palette of <paramref name="entries"/> slots for the picture.</summary>
    public static MenuPaletteResult Build(ReadOnlySpan<byte> rgba, int entries = 256, int maxColours = int.MaxValue)
    {
        var counts = new Dictionary<int, int>();
        bool transparent = false;

        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            // Transparency is one bit in the format, so every transparent pixel is the same entry.
            if (rgba[i + 3] < 128) { transparent = true; continue; }

            int key = (rgba[i] >> 3) << 10 | (rgba[i + 1] >> 3) << 5 | (rgba[i + 2] >> 3);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var colours = counts.Select(kv => new Colour(kv.Key >> 10, (kv.Key >> 5) & 31, kv.Key & 31, kv.Value))
                            .ToList();

        int distinct = colours.Count + (transparent ? 1 : 0);
        int room = Math.Max(1, Math.Min(entries, maxColours) - (transparent ? 1 : 0));

        var chosen = colours.Count <= room
            ? colours.Select(c => (c.R, c.G, c.B)).ToList()
            : MedianCut(colours, room);

        var palette = new byte[entries * 4];
        int at = 0;

        // Entry 0 is the transparent one, when there is one.
        if (transparent) at++;

        foreach (var (r, g, b) in chosen)
        {
            palette[at * 4] = Expand(r);
            palette[at * 4 + 1] = Expand(g);
            palette[at * 4 + 2] = Expand(b);
            palette[at * 4 + 3] = 255;
            at++;
        }

        int used = at;

        // Unused entries repeat the first opaque colour, so nothing matches them by accident.
        for (; at < entries; at++)
        {
            if (chosen.Count == 0) break;
            Array.Copy(palette, (transparent ? 1 : 0) * 4, palette, at * 4, 4);
        }

        return new MenuPaletteResult(palette, distinct, used, colours.Count > room, transparent);
    }

    /// <summary>
    /// The picture redrawn in at most <paramref name="colours"/> colours, picked the same way as a
    /// palette. Used before matching into an existing palette, so the result uses fewer entries.
    /// </summary>
    public static byte[] Reduce(ReadOnlySpan<byte> rgba, int colours)
    {
        var palette = Build(rgba, 256, colours);
        int first = palette.HasTransparency ? 1 : 0;
        var output = new byte[rgba.Length];
        var cache = new Dictionary<int, int>();

        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 128) continue;              // stays transparent black

            int r = rgba[i] >> 3, g = rgba[i + 1] >> 3, b = rgba[i + 2] >> 3;
            int key = r << 10 | g << 5 | b;

            if (!cache.TryGetValue(key, out int best))
            {
                int bestScore = int.MaxValue;
                for (int c = first; c < palette.PaletteEntries; c++)
                {
                    int dr = (palette.PaletteRgba[c * 4] >> 3) - r;
                    int dg = (palette.PaletteRgba[c * 4 + 1] >> 3) - g;
                    int db = (palette.PaletteRgba[c * 4 + 2] >> 3) - b;
                    int score = dr * dr + dg * dg + db * db;
                    if (score < bestScore) { bestScore = score; best = c; }
                }
                cache[key] = best;
            }

            output[i] = palette.PaletteRgba[best * 4];
            output[i + 1] = palette.PaletteRgba[best * 4 + 1];
            output[i + 2] = palette.PaletteRgba[best * 4 + 2];
            output[i + 3] = 255;
        }

        return output;
    }

    private static byte Expand(int five) => (byte)((five << 3) | (five >> 2));

    private static List<(int R, int G, int B)> MedianCut(List<Colour> colours, int target)
    {
        var boxes = new List<List<Colour>> { colours };

        while (boxes.Count < target)
        {
            // Split the box with the most error to give away: its widest channel times its pixel count.
            int pick = -1;
            long bestScore = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                var (_, range) = WidestChannel(boxes[i]);
                long score = (long)range * boxes[i].Sum(c => (long)c.Count);
                if (score > bestScore) { bestScore = score; pick = i; }
            }

            if (pick < 0) break;

            var box = boxes[pick];
            var (channel, _) = WidestChannel(box);
            box.Sort((a, b) => Channel(a, channel).CompareTo(Channel(b, channel)));

            // Split at the weighted median, keeping at least one colour on each side.
            long half = box.Sum(c => (long)c.Count) / 2, running = 0;
            int split = 1;
            for (int i = 0; i < box.Count - 1; i++)
            {
                running += box[i].Count;
                if (running >= half) { split = i + 1; break; }
            }

            boxes[pick] = box.GetRange(0, split);
            boxes.Add(box.GetRange(split, box.Count - split));
        }

        return boxes.Select(Average).Distinct().ToList();
    }

    private static int Channel(Colour c, int channel) => channel switch { 0 => c.R, 1 => c.G, _ => c.B };

    private static (int Channel, int Range) WidestChannel(List<Colour> box)
    {
        int best = 0, bestRange = -1;
        for (int ch = 0; ch < 3; ch++)
        {
            int min = int.MaxValue, max = int.MinValue;
            foreach (var c in box)
            {
                int v = Channel(c, ch);
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (max - min > bestRange) { bestRange = max - min; best = ch; }
        }
        return (best, bestRange);
    }

    private static (int R, int G, int B) Average(List<Colour> box)
    {
        long r = 0, g = 0, b = 0, n = 0;
        foreach (var c in box) { r += c.R * c.Count; g += c.G * c.Count; b += c.B * c.Count; n += c.Count; }
        return ((int)((r + n / 2) / n), (int)((g + n / 2) / n), (int)((b + n / 2) / n));
    }
}
