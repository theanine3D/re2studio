using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Re2.Core.Assets;

/// <summary>Small helpers for turning decoded pixel buffers into image files.</summary>
public static class ImageCodec
{
    /// <summary>Wraps a tightly packed RGBA8888 buffer as a PNG.</summary>
    public static byte[] RgbaToPng(byte[] rgba, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(rgba, width, height);
        using var ms = new System.IO.MemoryStream();
        image.Save(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
        return ms.ToArray();
    }
}
