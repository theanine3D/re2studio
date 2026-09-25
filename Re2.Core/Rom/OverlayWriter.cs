using System;
using System.Buffers.Binary;
using Re2.Core.Codecs;

namespace Re2.Core.Rom;

/// <summary>Writes a modified overlay image back into the cart.</summary>
public static class OverlayWriter
{
    /// <summary>
    /// Recompresses <paramref name="image"/> and stores it in <paramref name="entry"/>'s slot.
    /// </summary>
    public static bool TryReplace(RomFile rom, OverlayEntry entry, ReadOnlySpan<byte> image,
                                  out string error)
    {
        error = "";

        if (image.Length != entry.DecompressedSize)
        {
            error = $"Overlay {entry.Index} must decompress to exactly {entry.DecompressedSize:N0} " +
                    $"bytes; this image is {image.Length:N0}. The code in it refers to fixed " +
                    "addresses, so it cannot change size.";
            return false;
        }

        var stored = Zlib.Compress(image);

        if (stored.Length > entry.CompressedSize)
        {
            error = $"Overlay {entry.Index} recompresses to {stored.Length:N0} bytes but its slot " +
                    $"holds {entry.CompressedSize:N0}, {stored.Length - entry.CompressedSize:N0} " +
                    "too few. The overlays are packed end to end, so it cannot grow.";
            return false;
        }

        // Checked before anything is written, so a failure leaves the ROM untouched.
        if (!Zlib.TryDecompress(stored, out var back) || !back.AsSpan().SequenceEqual(image))
        {
            error = $"Overlay {entry.Index} did not survive a round trip through the compressor.";
            return false;
        }

        stored.CopyTo(rom.Data.AsSpan(entry.RomOffset));

        // Whatever the old, longer stream left behind is cleared, so nothing downstream can mistake
        // its tail for data.
        rom.Data.AsSpan(entry.RomOffset + stored.Length, entry.CompressedSize - stored.Length).Clear();

        int record = OverlayTable.Locate(rom.Data) + entry.Index * OverlayTable.RecordSize;
        BinaryPrimitives.WriteUInt32BigEndian(rom.Data.AsSpan(record + 4, 4), (uint)stored.Length);

        return true;
    }

    /// <summary>How much room is left in an overlay's slot once its current image is recompressed.</summary>
    public static int SpareBytes(RomFile rom, OverlayEntry entry)
    {
        if (!OverlayTable.TryDecompress(rom.Data, entry, out var image)) return 0;
        return entry.CompressedSize - Zlib.Compress(image).Length;
    }
}
