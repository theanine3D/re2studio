using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>Writes replacement backgrounds into the ROM in place.</summary>
public static class BackgroundWriter
{
    /// <summary>Marker plus length field; the smallest COM segment that can exist.</summary>
    private const int MinCommentSegment = 4;

    /// <summary>A segment's length field is 16-bit and counts itself.</summary>
    private const int MaxCommentPayload = 0xFFFF - 2;

    /// <summary>
    /// Replaces a background, padding the encoded image to exactly the original stored size.
    /// </summary>
    public static void Replace(RomFile rom, Background background, ReadOnlySpan<byte> newJpeg)
    {
        var padded = PadToExactSize(newJpeg, background.Length);
        padded.CopyTo(rom.Data.AsSpan(background.Offset));
    }

    /// <summary>
    /// Grows a JPEG to exactly <paramref name="targetSize"/> bytes without changing the decoded image.
    /// </summary>
    public static byte[] PadToExactSize(ReadOnlySpan<byte> jpeg, int targetSize)
    {
        if (jpeg.Length > targetSize)
            throw new ArgumentException(
                $"Image is {jpeg.Length:N0} bytes, which does not fit the {targetSize:N0} byte slot.", nameof(jpeg));

        int deficit = targetSize - jpeg.Length;
        if (deficit == 0) return jpeg.ToArray();

        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != JpegMarker.Soi)
            throw new ArgumentException("Not a JPEG stream (missing SOI).", nameof(jpeg));

        // Keep SOI and the leading APP0/JFIF segment exactly where they are: the ROM scanner (and
        // anything else looking for backgrounds) recognises images by that signature, and retail
        // data always has it. Padding therefore goes after the first header segment, not before it.
        int insertAt = FindInsertionPoint(jpeg);

        var output = new List<byte>(targetSize);
        output.AddRange(jpeg[..insertAt].ToArray());

        int remaining = deficit;
        while (remaining >= MinCommentSegment)
        {
            int payload = Math.Min(remaining - MinCommentSegment, MaxCommentPayload);
            output.Add(0xFF);
            output.Add(0xFE);                                   // COM
            int segmentLength = payload + 2;                    // length field counts itself
            output.Add((byte)(segmentLength >> 8));
            output.Add((byte)segmentLength);
            for (int i = 0; i < payload; i++) output.Add(0);
            remaining -= payload + MinCommentSegment;
        }

        // 1-3 bytes left over: 0xFF fill bytes are legal immediately before a marker, so put them
        // right before the next one.
        for (int i = 0; i < remaining; i++) output.Add(0xFF);

        output.AddRange(jpeg[insertAt..].ToArray());

        if (output.Count != targetSize)
            throw new InvalidOperationException($"Padding produced {output.Count} bytes, expected {targetSize}.");

        return output.ToArray();
    }

    /// <summary>
    /// Returns the offset just past SOI and the first length-bearing header segment, which is where
    /// padding can be inserted without disturbing the image's recognisable prefix.
    /// </summary>
    private static int FindInsertionPoint(ReadOnlySpan<byte> jpeg)
    {
        int pos = 2; // past SOI
        if (pos + 4 > jpeg.Length) return pos;
        if (jpeg[pos] != 0xFF) return pos;

        byte marker = jpeg[pos + 1];
        if (JpegMarker.IsStandalone(marker) || marker == JpegMarker.Sos) return pos;

        int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos + 2, 2));
        int next = pos + 2 + length;
        return length >= 2 && next <= jpeg.Length ? next : pos;
    }

    /// <summary>Reads back the trailer for a file and confirms it still describes it.</summary>
    public static bool VerifyTrailerIntact(RomFile rom, Background background)
    {
        int trailer = (background.Offset + background.Length + 1) & ~1;
        if (trailer + 8 > rom.Length) return false;
        uint size = BinaryPrimitives.ReadUInt32BigEndian(rom.Data.AsSpan(trailer + 4, 4));
        return size == (uint)background.Length;
    }
}
