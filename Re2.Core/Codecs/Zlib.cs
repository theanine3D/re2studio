using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Re2.Core.Codecs;

/// <summary>The zlib envelope RE2 N64 stores its compressed assets in.</summary>
public static class Zlib
{
    /// <summary>CM=8, CINFO=6, valid check bits: the header every compressed asset in the ROM uses.</summary>
    public static readonly byte[] Header = { 0x68, 0xDE };

    /// <summary>The window <see cref="Header"/> declares, and therefore the most any stream may reach back.</summary>
    public const int WindowSize = 16 * 1024;

    public const int HeaderSize = 2;
    public const int ChecksumSize = 4;

    /// <summary>Wraps <paramref name="data"/> in the game's zlib envelope.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        var deflate = RawDeflate.Compress(data);

        if (data.Length > WindowSize && !FitsWindow(deflate))
        {
            deflate = Deflate.Compress(data, WindowSize);
            if (!FitsWindow(deflate)) deflate = StoredBlocks(data);
        }

        var result = new byte[HeaderSize + deflate.Length + ChecksumSize];
        Header.CopyTo(result, 0);
        deflate.CopyTo(result, HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(HeaderSize + deflate.Length), Adler32(data));
        return result;
    }

    /// <summary>True when the stream never reaches back further than the declared window.</summary>
    public static bool FitsWindow(ReadOnlySpan<byte> deflate)
        => Inflate.TryInflateRaw(deflate, out _, out _, out int maxDistance) && maxDistance <= WindowSize;

    /// <summary>
    /// Reads one of the game's zlib streams, verifying the checksum the way the console's zlib does.
    /// </summary>
    public static bool TryDecompress(ReadOnlySpan<byte> stored, out byte[] output)
    {
        output = Array.Empty<byte>();
        if (stored.Length < HeaderSize + ChecksumSize) return false;

        if (!Inflate.TryInflateRaw(stored[HeaderSize..], out var decoded, out int consumed)) return false;

        int at = HeaderSize + consumed;
        if (at + ChecksumSize > stored.Length) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(stored[at..]) != Adler32(decoded)) return false;

        output = decoded;
        return true;
    }

    /// <summary>Deflate that stores its input verbatim, so it needs no window to read back.</summary>
    private static byte[] StoredBlocks(ReadOnlySpan<byte> data)
    {
        const int Max = 0xFFFF;
        int blocks = Math.Max(1, (data.Length + Max - 1) / Max);
        var result = new byte[blocks * 5 + data.Length];

        int read = 0, write = 0;
        for (int i = 0; i < blocks; i++)
        {
            int take = Math.Min(Max, data.Length - read);
            bool last = i == blocks - 1;

            // BFINAL in bit 0 and BTYPE=00 in bits 1-2; a stored block then pads to the byte boundary.
            result[write++] = (byte)(last ? 1 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(write), (ushort)take);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(write + 2), (ushort)~take);
            write += 4;

            data.Slice(read, take).CopyTo(result.AsSpan(write));
            read += take;
            write += take;
        }

        return result;
    }

    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;

        foreach (byte x in data)
        {
            a += x;
            if (a >= Mod) a -= Mod;
            b += a;
            if (b >= Mod) b -= Mod;
        }

        return (b << 16) | a;
    }
}
