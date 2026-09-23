using System;
using System.Buffers.Binary;

namespace Re2.Core.Rom;

public enum CicChip
{
    Unknown = 0,
    Cic6101 = 6101,
    Cic6102 = 6102,
    Cic6103 = 6103,
    Cic6105 = 6105,
    Cic6106 = 6106
}

/// <summary>N64 boot checksum (the pair of words at header offsets 0x10/0x14).</summary>
public static class N64Crc
{
    private const int ChecksumStart = 0x1000;
    private const int ChecksumLength = 0x100000;
    private const int HeaderSize = 0x40;

    private static uint Seed(CicChip cic) => cic switch
    {
        CicChip.Cic6101 => 0x3F,
        CicChip.Cic6102 => 0xF8CA4DDC,
        CicChip.Cic6103 => 0xA3886759,
        CicChip.Cic6105 => 0xDF26F436,
        CicChip.Cic6106 => 0x1FEA617A,
        _ => 0xF8CA4DDC
    };

    /// <summary>Identifies the CIC chip from the CRC32 of the IPL3 boot code (header 0x40..0x1000).</summary>
    public static CicChip DetectCic(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < ChecksumStart) return CicChip.Unknown;
        uint crc = Crc32(rom[HeaderSize..ChecksumStart]);
        return crc switch
        {
            0x6170A4A1 => CicChip.Cic6101,
            0x90BB6CB5 => CicChip.Cic6102,
            0x0B050EE0 => CicChip.Cic6103,
            0x98BC2C86 => CicChip.Cic6105,
            0xACC8580A => CicChip.Cic6106,
            _ => CicChip.Unknown
        };
    }

    private static uint Rol(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    /// <summary>Computes the (crc1, crc2) pair that belongs at header offsets 0x10 and 0x14.</summary>
    public static (uint Crc1, uint Crc2) Compute(ReadOnlySpan<byte> rom, CicChip cic)
    {
        if (rom.Length < ChecksumStart + ChecksumLength)
            throw new ArgumentException("ROM is too small to checksum (needs at least 0x101000 bytes).", nameof(rom));

        uint seed = Seed(cic);
        uint t1 = seed, t2 = seed, t3 = seed, t4 = seed, t5 = seed, t6 = seed;

        for (int i = 0; i < ChecksumLength; i += 4)
        {
            uint d = BinaryPrimitives.ReadUInt32BigEndian(rom.Slice(ChecksumStart + i, 4));

            if (unchecked(t6 + d) < t6) t4++;
            t6 += d;
            t3 ^= d;

            uint r = Rol(d, (int)(d & 0x1F));
            t5 += r;

            if (t2 > d) t2 ^= r;
            else t2 ^= t6 ^ d;

            if (cic == CicChip.Cic6105)
                t1 += BinaryPrimitives.ReadUInt32BigEndian(rom.Slice(HeaderSize + 0x0710 + (i & 0xFF), 4)) ^ d;
            else
                t1 += t5 ^ d;
        }

        return cic == CicChip.Cic6106
            ? (unchecked(t6 * t4) ^ t3, unchecked(t5 * t2) ^ t1)
            : (t6 ^ t4 ^ t3, t5 ^ t2 ^ t1);
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
