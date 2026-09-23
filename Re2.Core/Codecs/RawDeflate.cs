using System;
using System.IO;
using System.IO.Compression;

namespace Re2.Core.Codecs;

/// <summary>Raw DEFLATE (no zlib/gzip wrapper) as used by RE2 N64's code overlays.</summary>
public static class RawDeflate
{
    /// <summary>Inflates a raw deflate stream, reporting exactly how many input bytes it consumed.</summary>
    public static bool TryDecompress(ReadOnlySpan<byte> input, out byte[] output, out int consumed, int maxOutput = 64 << 20)
        => Inflate.TryInflateRaw(input, out output, out consumed, maxOutput);

    /// <summary>Inflates a raw deflate stream or throws.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input, out int consumed)
    {
        if (!TryDecompress(input, out var output, out consumed))
            throw new InvalidDataException("Not a valid raw deflate stream.");
        return output;
    }

    /// <summary>Compresses to a raw deflate stream. Defaults to the smallest output the BCL can produce.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> input, CompressionLevel level = CompressionLevel.SmallestSize)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, level, leaveOpen: true))
            ds.Write(input);
        return ms.ToArray();
    }
}
