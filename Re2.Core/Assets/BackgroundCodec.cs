using System;
using System.Collections.Generic;
using System.IO;
using Re2.Core.Formats;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Re2.Core.Assets;

/// <summary>Outcome of encoding a replacement background.</summary>
public sealed record EncodedBackground(byte[] Data, int Quality, bool FitsSlot, int SlotCapacity)
{
    public int Size => Data.Length;

    /// <summary>The image matched the stored one, so its original bytes were kept.</summary>
    public bool Unchanged { get; init; }
}

/// <summary>Converts between the ROM's stored JPEGs and editable PNGs.</summary>
public static class BackgroundCodec
{
    /// <summary>Chroma subsampling used by every retail background.</summary>
    public const JpegColorType RetailColorType = JpegColorType.YCbCrRatio420;

    /// <summary>Decodes a stored background to PNG bytes, without correcting anything.</summary>
    public static byte[] JpegToPng(ReadOnlySpan<byte> jpeg)
    {
        using var image = Image.Load<Rgba32>(jpeg.ToArray());
        return ToPng(image);
    }

    private static byte[] ToPng(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
        return ms.ToArray();
    }

    /// <summary>Decodes a stored background to raw RGBA pixels, without correcting anything.</summary>
    public static (byte[] Pixels, int Width, int Height) JpegToRgba(ReadOnlySpan<byte> jpeg)
    {
        using var image = Image.Load<Rgba32>(jpeg.ToArray());
        return ToRgba(image);
    }

    private static (byte[] Pixels, int Width, int Height) ToRgba(Image<Rgba32> image)
    {
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return (pixels, image.Width, image.Height);
    }

    /// <summary>
    /// Encodes an image as a baseline JPEG at a fixed quality, resizing is never done implicitly --
    /// callers are responsible for supplying correct dimensions.
    /// </summary>
    public static byte[] EncodeBaseline(Image<Rgba32> image, int quality)
    {
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = quality, ColorType = RetailColorType });
        return ms.ToArray();
    }

    /// <summary>
    /// Encodes a replacement image to fit a slot, stepping quality down until it does.
    /// </summary>
    public static EncodedBackground EncodeToFit(Image<Rgba32> image, int slotCapacity,
        int startQuality = 90, int minQuality = 30, int step = 5)
    {
        byte[] best = EncodeBaseline(image, startQuality);
        if (best.Length <= slotCapacity)
            return new EncodedBackground(best, startQuality, true, slotCapacity);

        for (int q = startQuality - step; q >= minQuality; q -= step)
        {
            var data = EncodeBaseline(image, q);
            if (data.Length <= slotCapacity)
                return new EncodedBackground(data, q, true, slotCapacity);
            best = data;
        }

        return new EncodedBackground(best, minQuality, false, slotCapacity);
    }

    /// <summary>
    /// Loads a replacement image from disk and encodes it for a slot, verifying it matches the
    /// dimensions the game expects for that background.
    /// </summary>
    public static EncodedBackground EncodeReplacement(string path, Background target,
        int startQuality = 90, bool requireExactSize = true)
    {
        using var image = Image.Load<Rgba32>(path);

        if (requireExactSize && (image.Width != target.Width || image.Height != target.Height))
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is {image.Width}x{image.Height} but background " +
                $"{target.Index} is {target.Width}x{target.Height}. The game reads a fixed frame size " +
                $"per room, so replacements must match exactly.");

        return EncodeToFit(image, target.SlotCapacity, startQuality);
    }

    /// <summary>
    /// Encodes a replacement background for a <b>project folder</b>, aiming at the original's size.
    /// </summary>
    public static EncodedBackground EncodeForProject(string path, Background target,
        ReadOnlySpan<byte> current, int budget)
    {
        using var image = Image.Load<Rgba32>(path);

        if (image.Width != target.Width || image.Height != target.Height)
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is {image.Width}x{image.Height} but background " +
                $"{target.Index} is {target.Width}x{target.Height}. The game reads a fixed frame size " +
                $"per room, so replacements must match exactly.");

        if (!current.IsEmpty && MatchesDecoded(image, current))
            return new EncodedBackground(current.ToArray(), Quality: 0, FitsSlot: current.Length <= budget,
                                         SlotCapacity: budget) { Unchanged = true };

        return EncodeClosestUnder(image, budget);
    }

    /// <summary>
    /// The highest quality whose encoding is no larger than <paramref name="budget"/>.
    /// </summary>
    public static EncodedBackground EncodeClosestUnder(Image<Rgba32> image, int budget,
        int minQuality = 10, int maxQuality = 95)
    {
        byte[]? best = null;
        int bestQuality = 0;

        int low = minQuality, high = maxQuality;
        while (low <= high)
        {
            int q = (low + high) / 2;
            var data = EncodeBaseline(image, q);

            if (data.Length <= budget)
            {
                best = data;
                bestQuality = q;
                low = q + 1;
            }
            else
            {
                high = q - 1;
            }
        }

        if (best is not null) return new EncodedBackground(best, bestQuality, true, budget);

        // Nothing fits: hand back the smallest, and let the caller say so.
        return new EncodedBackground(EncodeBaseline(image, minQuality), minQuality, false, budget);
    }

    private static bool MatchesDecoded(Image<Rgba32> image, ReadOnlySpan<byte> jpeg)
    {
        var (pixels, width, height) = JpegToRgba(jpeg);
        if (width != image.Width || height != image.Height) return false;

        var incoming = new byte[width * height * 4];
        image.CopyPixelDataTo(incoming);

        // Alpha is ignored: a PNG editor may write it either way, and a JPEG has none.
        for (int i = 0; i < incoming.Length; i += 4)
            if (incoming[i] != pixels[i] || incoming[i + 1] != pixels[i + 1] || incoming[i + 2] != pixels[i + 2])
                return false;

        return true;
    }

    /// <summary>Confirms a byte sequence is a baseline JFIF the game can decode.</summary>
    public static bool IsGameDecodable(ReadOnlySpan<byte> jpeg, out string reason)
    {
        if (!Jfif.TryParse(jpeg, 0, out var image, out _))
        {
            reason = "not a well-formed JFIF stream";
            return false;
        }

        if (!image.IsBaseline)
        {
            reason = "not baseline sequential (SOF0); libjpeg v5 in the ROM cannot decode it";
            return false;
        }

        if (image.Length != jpeg.Length)
        {
            reason = $"trailing data after EOI ({jpeg.Length - image.Length} bytes)";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
