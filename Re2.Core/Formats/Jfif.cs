using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Re2.Core.Formats;

/// <summary>Marker codes we care about. The low byte of an 0xFFxx marker.</summary>
public static class JpegMarker
{
    public const byte Soi = 0xD8;
    public const byte Eoi = 0xD9;
    public const byte Sos = 0xDA;
    public const byte Dqt = 0xDB;
    public const byte Dnl = 0xDC;
    public const byte Dri = 0xDD;
    public const byte App0 = 0xE0;
    public const byte Sof0 = 0xC0;  // baseline sequential
    public const byte Sof1 = 0xC1;  // extended sequential
    public const byte Sof2 = 0xC2;  // progressive - NOT decodable by libjpeg v5 baseline path
    public const byte Dht = 0xC4;
    public const byte Rst0 = 0xD0;
    public const byte Rst7 = 0xD7;

    public static bool IsStandalone(byte m) => m == Soi || m == Eoi || (m >= Rst0 && m <= Rst7) || m == 0x01;
}

public sealed record JfifSegment(byte Marker, int Offset, int Length);

/// <summary>A baseline JFIF image located inside the ROM.</summary>
public sealed record JfifImage(
    int Offset,
    int Length,
    int Width,
    int Height,
    int Components,
    byte SamplingFactors,
    bool IsBaseline)
{
    public int End => Offset + Length;

    /// <summary>Chroma subsampling as libjpeg sees it, derived from the luma component's H/V factors.</summary>
    public string Subsampling => SamplingFactors switch
    {
        0x11 => "4:4:4",
        0x21 => "4:2:2",
        0x22 => "4:2:0",
        0x12 => "4:4:0",
        _ => $"0x{SamplingFactors:X2}"
    };

    public override string ToString() => $"{Width}x{Height} {Subsampling} {Length:N0}B @0x{Offset:X7}";
}

/// <summary>
/// Minimal JFIF reader: enough to locate, validate and measure the baseline JPEGs that RE2 stores
/// verbatim in the ROM.
/// </summary>
public static class Jfif
{
    /// <summary>Parses a JFIF image starting at <paramref name="offset"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, int offset, out JfifImage image, out List<JfifSegment>? segments)
    {
        image = null!;
        segments = null;

        if (offset < 0 || offset + 4 > data.Length) return false;
        if (data[offset] != 0xFF || data[offset + 1] != JpegMarker.Soi) return false;

        var segs = new List<JfifSegment>();
        int pos = offset + 2;
        int width = 0, height = 0, components = 0;
        byte sampling = 0;
        bool sawSof = false, baseline = false;

        while (true)
        {
            if (pos + 2 > data.Length) return false;
            if (data[pos] != 0xFF) return false;

            byte marker = data[pos + 1];

            // Fill bytes: any run of 0xFF before a marker is padding.
            if (marker == 0xFF) { pos++; continue; }

            if (JpegMarker.IsStandalone(marker)) { pos += 2; continue; }

            if (pos + 4 > data.Length) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 2, 2));
            if (length < 2 || pos + 2 + length > data.Length) return false;

            segs.Add(new JfifSegment(marker, pos, length));

            if (marker is JpegMarker.Sof0 or JpegMarker.Sof1 or JpegMarker.Sof2
                or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (length < 8) return false;
                int p = pos + 4;
                // p+0 precision, p+1..2 height, p+3..4 width, p+5 component count
                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p + 1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p + 3, 2));
                components = data[p + 5];
                if (width == 0 || height == 0 || components is 0 or > 4) return false;
                if (length < 8 + components * 3) return false;
                sampling = data[p + 7]; // first component's H/V nibbles
                sawSof = true;
                baseline = marker == JpegMarker.Sof0;
            }
            else if (marker == JpegMarker.Sos)
            {
                if (!sawSof) return false;
                int scanStart = pos + 2 + length;
                if (!TryFindEoi(data, scanStart, out int eoiEnd)) return false;

                image = new JfifImage(offset, eoiEnd - offset, width, height, components, sampling, baseline);
                segments = segs;
                return true;
            }

            pos += 2 + length;
        }
    }

    /// <summary>
    /// Walks entropy-coded scan data to the terminating EOI, honouring 0xFF00 byte stuffing and
    /// restart markers. Returns the offset just past the EOI.
    /// </summary>
    private static bool TryFindEoi(ReadOnlySpan<byte> data, int start, out int end)
    {
        end = 0;
        for (int i = start; i + 1 < data.Length; i++)
        {
            if (data[i] != 0xFF) continue;
            byte next = data[i + 1];

            if (next == 0x00) { i++; continue; }                                   // stuffed literal 0xFF
            if (next == 0xFF) continue;                                            // fill byte
            if (next >= JpegMarker.Rst0 && next <= JpegMarker.Rst7) { i++; continue; } // restart

            if (next == JpegMarker.Eoi) { end = i + 2; return true; }

            // Any other marker inside the scan means this is not the simple single-scan baseline
            // image we expect. DNL is the one legal exception; treat everything else as a failure.
            if (next == JpegMarker.Dnl) { i += 1; continue; }
            return false;
        }
        return false;
    }

    /// <summary>
    /// Scans a byte range for every well-formed JFIF image, skipping past each one it finds.
    /// </summary>
    public static List<JfifImage> ScanRange(ReadOnlySpan<byte> data, int start, int end)
    {
        var found = new List<JfifImage>();
        int i = Math.Max(0, start);
        int limit = Math.Min(end, data.Length);

        while (i + 4 <= limit)
        {
            // Anchor on SOI followed by any marker rather than specifically APP0: retail images all
            // start SOI+APP0, but one we have re-encoded may carry a padding segment first.
            if (data[i] == 0xFF && data[i + 1] == JpegMarker.Soi && data[i + 2] == 0xFF
                && TryParse(data, i, out var image, out _))
            {
                found.Add(image);
                i = image.End;
                continue;
            }
            i++;
        }

        return found;
    }
}
