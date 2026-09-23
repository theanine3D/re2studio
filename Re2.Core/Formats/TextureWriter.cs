using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Re2.Core.Formats;

/// <summary>What quantising an image cost, so a caller can tell the user.</summary>
public sealed record TextureImportReport(
    int Width, int Height, int PaletteCount, int BitsPerPixel,
    int DistinctColours, int PaletteEntriesUsed, bool Lossless, double MeanError)
{
    public override string ToString() =>
        $"{Width}x{Height} CI{BitsPerPixel}, {DistinctColours} distinct colours -> " +
        $"{PaletteEntriesUsed}/{PaletteCount} palette entries" +
        (Lossless ? ", exact" : $", mean error {MeanError:0.0}/255");
}

/// <summary>
/// Encodes an RGBA image into the game's texture format -- the write side of <see cref="TextureFile"/>.
/// </summary>
public static class TextureWriter
{
    /// <summary>A pixel is opaque at or above this alpha; the format has no middle ground.</summary>
    public const byte AlphaThreshold = 128;

    /// <summary>
    /// Re-encodes <paramref name="rgba"/> into the shape of <paramref name="original"/>.
    /// </summary>
    public static (byte[] Data, TextureImportReport Report) Replace(TextureFile original, byte[] rgba, int width, int height)
    {
        if (width != original.Width || height != original.Height)
            throw new InvalidDataException(
                $"The replacement is {width}x{height} but the texture is {original.Width}x{original.Height}. " +
                "Dimensions are fixed: the geometry's UVs and the palette's bit depth both depend on them.");

        if (rgba.Length < width * height * 4)
            throw new InvalidDataException($"Expected {width * height * 4:N0} bytes of RGBA, got {rgba.Length:N0}.");

        return original.IsPaletted
            ? WritePaletted(original, rgba, width, height)
            : WriteGreyscale(original, rgba, width, height);
    }

    // ---- paletted (CI4 / CI8) ---------------------------------------------

    private static (byte[], TextureImportReport) WritePaletted(TextureFile original, byte[] rgba, int width, int height)
    {
        int count = width * height;

        // Reduce to the format's own precision before doing anything else.
        var reduced = new ushort[count];
        for (int i = 0; i < count; i++)
            reduced[i] = ToRgba5551(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);

        var distinct = new HashSet<ushort>(reduced);
        ushort[] palette;
        bool lossless;

        if (distinct.Count <= original.PaletteCount)
        {
            // Fits already, so keep the exact colours and lose nothing.
            palette = distinct.OrderBy(c => c).ToArray();
            lossless = true;
        }
        else
        {
            palette = MedianCut(reduced, original.PaletteCount);
            lossless = false;
        }

        var lookup = BuildLookup(distinct, palette);

        int paletteBytes = original.PaletteCount * 2;
        int pixelBytes = original.BitsPerPixel == 4 ? (count + 1) / 2 : count;
        var output = new byte[TextureFile.HeaderSize + paletteBytes + pixelBytes];

        WriteHeader(output, original, width);

        for (int i = 0; i < original.PaletteCount; i++)
            BinaryPrimitives.WriteUInt16BigEndian(
                output.AsSpan(TextureFile.HeaderSize + i * 2, 2),
                i < palette.Length ? palette[i] : (ushort)0);

        int pixelsAt = TextureFile.HeaderSize + paletteBytes;
        double error = 0;

        for (int i = 0; i < count; i++)
        {
            int index = lookup[reduced[i]];
            error += Distance(reduced[i], palette[index]);

            if (original.BitsPerPixel == 8) output[pixelsAt + i] = (byte)index;
            else if ((i & 1) == 0) output[pixelsAt + (i >> 1)] |= (byte)(index << 4);
            else output[pixelsAt + (i >> 1)] |= (byte)(index & 0x0F);
        }

        var report = new TextureImportReport(width, height, original.PaletteCount, original.BitsPerPixel,
            distinct.Count, palette.Length, lossless, lossless ? 0 : Math.Sqrt(error / count));

        return (output, report);
    }

    /// <summary>Formats 5 and 6 carry no palette; the pixel byte is the grey level.</summary>
    private static (byte[], TextureImportReport) WriteGreyscale(TextureFile original, byte[] rgba, int width, int height)
    {
        int count = width * height;
        var output = new byte[TextureFile.HeaderSize + count];
        WriteHeader(output, original, width);

        for (int i = 0; i < count; i++)
        {
            // Rec. 601 luma, which is what a greyscale conversion should use rather than a flat mean.
            int luma = (rgba[i * 4] * 299 + rgba[i * 4 + 1] * 587 + rgba[i * 4 + 2] * 114) / 1000;
            output[TextureFile.HeaderSize + i] = (byte)Math.Clamp(luma, 0, 255);
        }

        return (output, new TextureImportReport(width, height, 0, 8, 0, 0, false, 0));
    }

    private static void WriteHeader(byte[] output, TextureFile original, int width)
    {
        var header = output.AsSpan(0, TextureFile.HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(header, TextureFile.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)width);

        // @4 and @6 disagree about which holds the height across the retail set, so both are copied
        // from the texture being replaced rather than recomputed from the image.
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], original.DeclaredHeight);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], original.Field6);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], (ushort)original.Format);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)original.PaletteCount);
        BinaryPrimitives.WriteUInt16BigEndian(header[12..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(header[14..], 0);
    }

    // ---- colour ------------------------------------------------------------

    public static ushort ToRgba5551(byte r, byte g, byte b, byte a)
    {
        int r5 = (r * 31 + 127) / 255;
        int g5 = (g * 31 + 127) / 255;
        int b5 = (b * 31 + 127) / 255;
        return (ushort)((r5 << 11) | (g5 << 6) | (b5 << 1) | (a >= AlphaThreshold ? 1 : 0));
    }

    private static (int R, int G, int B, int A) Unpack(ushort c)
        => ((c >> 11) & 0x1F, (c >> 6) & 0x1F, (c >> 1) & 0x1F, c & 1);

    /// <summary>Squared distance in 5-bit space.</summary>
    private static int Distance(ushort x, ushort y)
    {
        var (r1, g1, b1, a1) = Unpack(x);
        var (r2, g2, b2, a2) = Unpack(y);
        int dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return dr * dr + dg * dg + db * db + (a1 != a2 ? 4096 : 0);
    }

    private static Dictionary<ushort, int> BuildLookup(HashSet<ushort> distinct, ushort[] palette)
    {
        var lookup = new Dictionary<ushort, int>(distinct.Count);

        foreach (ushort colour in distinct)
        {
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                int d = Distance(colour, palette[i]);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = i;
                if (d == 0) break;
            }
            lookup[colour] = best;
        }

        return lookup;
    }

    /// <summary>Median cut over the 5-bit colours, weighted by how often each appears.</summary>
    private static ushort[] MedianCut(ushort[] pixels, int maxColours)
    {
        var histogram = new Dictionary<ushort, int>();
        foreach (ushort c in pixels) histogram[c] = histogram.GetValueOrDefault(c) + 1;

        var opaque = histogram.Where(kv => (kv.Key & 1) != 0).ToList();
        var clear = histogram.Where(kv => (kv.Key & 1) == 0).ToList();

        var palette = new List<ushort>();

        // One entry is enough for everything invisible: alpha 0 hides the colour anyway.
        if (clear.Count > 0) palette.Add(0);

        int budget = maxColours - palette.Count;
        if (opaque.Count == 0) return palette.ToArray();

        var boxes = new List<List<KeyValuePair<ushort, int>>> { opaque };

        while (boxes.Count < budget)
        {
            // Split whichever box spans the most in its widest channel.
            int target = -1, bestSpan = 0, bestChannel = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                var (span, channel) = WidestChannel(boxes[i]);
                if (span <= bestSpan) continue;
                bestSpan = span; target = i; bestChannel = channel;
            }

            if (target < 0) break;

            var box = boxes[target];
            box.Sort((x, y) => Channel(x.Key, bestChannel).CompareTo(Channel(y.Key, bestChannel)));

            // Split at the weighted median so both halves carry a similar number of pixels.
            int total = box.Sum(e => e.Value), running = 0, cut = 0;
            for (int i = 0; i < box.Count - 1; i++)
            {
                running += box[i].Value;
                if (running * 2 < total) continue;
                cut = i + 1;
                break;
            }
            if (cut <= 0 || cut >= box.Count) cut = box.Count / 2;

            boxes[target] = box.Take(cut).ToList();
            boxes.Add(box.Skip(cut).ToList());
        }

        foreach (var box in boxes)
        {
            long weight = box.Sum(e => (long)e.Value);
            if (weight == 0) continue;
            long r = 0, g = 0, b = 0;
            foreach (var (colour, n) in box.Select(e => (e.Key, e.Value)))
            {
                var (cr, cg, cb, _) = Unpack(colour);
                r += (long)cr * n; g += (long)cg * n; b += (long)cb * n;
            }
            palette.Add((ushort)(((r / weight) << 11) | ((g / weight) << 6) | ((b / weight) << 1) | 1));
        }

        return palette.ToArray();
    }

    private static int Channel(ushort colour, int channel)
    {
        var (r, g, b, _) = Unpack(colour);
        return channel switch { 0 => r, 1 => g, _ => b };
    }

    private static (int Span, int Channel) WidestChannel(List<KeyValuePair<ushort, int>> box)
    {
        int bestSpan = 0, bestChannel = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var entry in box)
            {
                int v = Channel(entry.Key, channel);
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
            if (hi - lo <= bestSpan) continue;
            bestSpan = hi - lo;
            bestChannel = channel;
        }
        return (bestSpan, bestChannel);
    }
}
