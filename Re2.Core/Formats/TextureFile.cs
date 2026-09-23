using System;
using System.Buffers.Binary;
using System.IO;

namespace Re2.Core.Formats;

/// <summary>
/// A texture asset. 1,053 of them, asset ids 5922..6975, identified by the 0xBE1E magic.
/// </summary>
public sealed class TextureFile
{
    public const ushort Magic = 0xBE1E;
    public const int HeaderSize = 16;

    public int Width { get; }
    public int Height { get; }
    public ushort DeclaredHeight { get; }
    public ushort Field6 { get; }
    public int Format { get; }
    public int PaletteCount { get; }

    /// <summary>4 for 16-colour textures, 8 otherwise.</summary>
    public int BitsPerPixel => PaletteCount == 16 ? 4 : 8;

    private readonly byte[] _data;

    /// <summary>True when the texture carries its own palette rather than being greyscale.</summary>
    public bool IsPaletted => PaletteCount > 0;

    /// <summary>The palette directly follows the header.</summary>
    public int PaletteOffset => HeaderSize;

    public int PixelDataOffset => HeaderSize + PaletteCount * 2;

    private TextureFile(byte[] data, int width, int height, ushort declaredHeight, ushort field6, int format, int paletteCount)
    {
        _data = data;
        Width = width;
        Height = height;
        DeclaredHeight = declaredHeight;
        Field6 = field6;
        Format = format;
        PaletteCount = paletteCount;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    public static bool LooksLikeTexture(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || U16(data, 0) != Magic) return false;

        int width = U16(data, 2);
        int paletteCount = U16(data, 10);
        if (width <= 0 || width > 4096) return false;

        int pixelBytes = data.Length - HeaderSize - paletteCount * 2;
        if (pixelBytes <= 0) return false;

        int pixels = paletteCount == 16 ? pixelBytes * 2 : pixelBytes;
        return pixels % width == 0 && pixels / width is > 0 and <= 4096;
    }

    public static bool TryParse(byte[] data, out TextureFile texture)
    {
        texture = null!;
        if (!LooksLikeTexture(data)) return false;
        texture = Parse(data);
        return true;
    }

    public static TextureFile Parse(byte[] data)
    {
        if (!LooksLikeTexture(data))
            throw new InvalidDataException("Not a texture asset (bad magic, dimensions or length).");

        int width = U16(data, 2);
        int paletteCount = U16(data, 10);
        int pixelBytes = data.Length - HeaderSize - paletteCount * 2;
        int pixels = paletteCount == 16 ? pixelBytes * 2 : pixelBytes;

        return new TextureFile(data, width, pixels / width, U16(data, 4), U16(data, 6), U16(data, 8), paletteCount);
    }

    /// <summary>Raw pixel bytes, still packed when the texture is 4bpp.</summary>
    public ReadOnlySpan<byte> PackedPixels => _data.AsSpan(PixelDataOffset, _data.Length - PixelDataOffset);

    /// <summary>Palette index of one pixel, unpacking the nibble for 4bpp textures.</summary>
    public int PixelIndex(int x, int y)
    {
        int i = y * Width + x;
        var packed = PackedPixels;
        if (BitsPerPixel == 8) return packed[i];
        byte b = packed[i >> 1];
        return (i & 1) == 0 ? b >> 4 : b & 0x0F;
    }

    /// <summary>Palette entry as RGBA5551, or 0 when the texture has no palette.</summary>
    public ushort PaletteEntry(int index)
        => index >= 0 && index < PaletteCount ? U16(_data, PaletteOffset + index * 2) : (ushort)0;

    /// <summary>Expands one RGBA5551 word to 8-bit RGBA.</summary>
    public static (byte R, byte G, byte B, byte A) Rgba5551ToRgba8(ushort value)
    {
        int r5 = (value >> 11) & 0x1F;
        int g5 = (value >> 6) & 0x1F;
        int b5 = (value >> 1) & 0x1F;
        int a1 = value & 1;

        static byte Expand(int v) => (byte)((v << 3) | (v >> 2));
        return (Expand(r5), Expand(g5), Expand(b5), a1 != 0 ? (byte)255 : (byte)0);
    }

    /// <summary>Decodes to a tightly packed RGBA8888 buffer, row-major from the top left.</summary>
    public byte[] ToRgba()
    {
        var output = new byte[Width * Height * 4];

        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int index = PixelIndex(x, y);
            byte r, g, b, a;

            if (IsPaletted) (r, g, b, a) = Rgba5551ToRgba8(PaletteEntry(index));
            else { r = g = b = (byte)index; a = 255; }   // greyscale, no palette

            int o = (y * Width + x) * 4;
            output[o] = r;
            output[o + 1] = g;
            output[o + 2] = b;
            output[o + 3] = a;
        }

        return output;
    }

    public override string ToString() =>
        $"{Width}x{Height} fmt {Format} " + (IsPaletted ? $"CI{BitsPerPixel}/{PaletteCount}" : "I8");
}
