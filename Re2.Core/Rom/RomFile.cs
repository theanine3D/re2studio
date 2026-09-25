using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Re2.Core.Rom;

public enum RomByteOrder
{
    /// <summary>Big endian, the native cart order. Magic 80 37 12 40. Usually .z64</summary>
    BigEndian,
    /// <summary>Byte-swapped pairs. Magic 37 80 40 12. Usually .v64</summary>
    ByteSwapped,
    /// <summary>Little endian words. Magic 40 12 37 80. Usually .n64</summary>
    LittleEndian
}

/// <summary>
/// An N64 ROM image held in memory, always normalised to big-endian regardless of the source file's
/// byte order. All offsets used elsewhere in this library are big-endian cart offsets.
/// </summary>
public sealed class RomFile
{
    public byte[] Data { get; }
    public RomByteOrder SourceByteOrder { get; }
    public string? SourcePath { get; }

    private RomFile(byte[] data, RomByteOrder order, string? path)
    {
        Data = data;
        SourceByteOrder = order;
        SourcePath = path;
    }

    public int Length => Data.Length;

    public static RomFile Load(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        var order = DetectByteOrder(raw);
        Normalize(raw, order);
        return new RomFile(raw, order, path);
    }

    /// <summary>Returns a copy of this ROM padded out to <paramref name="newLength"/>.</summary>
    public RomFile Expand(int newLength)
    {
        if (newLength < Length)
            throw new ArgumentException($"Cannot shrink a ROM: {newLength:N0} < {Length:N0}.");
        if (newLength > MaxAddressableLength)
            throw new ArgumentException(
                $"{newLength:N0} bytes exceeds the {MaxAddressableLength:N0} the PI cart window can address.");

        var grown = new byte[newLength];
        Data.CopyTo(grown, 0);
        return new RomFile(grown, SourceByteOrder, SourcePath);
    }

    /// <summary>Retail cart size, and the point past which expansion is uncharted.</summary>
    public const int RetailLength = 0x4000000;

    /// <summary>
    /// PI domain 1 runs from physical 0x10000000 to 0x1FBFFFFF, so this is the most a cart image
    /// could be addressed as, whatever hardware actually tolerates.
    /// </summary>
    public const int MaxAddressableLength = 0x1FC00000 - 0x10000000;

    public static RomFile FromBytes(byte[] data)
    {
        var order = DetectByteOrder(data);
        Normalize(data, order);
        return new RomFile(data, order, null);
    }

    public static RomByteOrder DetectByteOrder(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) throw new InvalidDataException("File is too small to be an N64 ROM.");
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        return magic switch
        {
            0x80371240 => RomByteOrder.BigEndian,
            0x37804012 => RomByteOrder.ByteSwapped,
            0x40123780 => RomByteOrder.LittleEndian,
            _ => throw new InvalidDataException($"Unrecognised N64 ROM magic 0x{magic:X8}.")
        };
    }

    private static void Normalize(byte[] data, RomByteOrder order)
    {
        switch (order)
        {
            case RomByteOrder.BigEndian:
                break;
            case RomByteOrder.ByteSwapped:
                for (int i = 0; i + 1 < data.Length; i += 2)
                    (data[i], data[i + 1]) = (data[i + 1], data[i]);
                break;
            case RomByteOrder.LittleEndian:
                for (int i = 0; i + 3 < data.Length; i += 4)
                {
                    (data[i], data[i + 3]) = (data[i + 3], data[i]);
                    (data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
                }
                break;
        }
    }

    // ---- header accessors -------------------------------------------------

    public uint ClockRate => ReadU32(0x04);
    public uint EntryPoint => ReadU32(0x08);
    public uint Release => ReadU32(0x0C);
    public uint Crc1 { get => ReadU32(0x10); set => WriteU32(0x10, value); }
    public uint Crc2 { get => ReadU32(0x14); set => WriteU32(0x14, value); }

    public string InternalName => Encoding.ASCII.GetString(Data, 0x20, 20).TrimEnd('\0', ' ');

    /// <summary>Media format: 'N' cartridge, 'D' 64DD disk, 'C' cartridge part of an expandable game.</summary>
    public char MediaFormat => (char)Data[0x3B];

    public string CartridgeId => Encoding.ASCII.GetString(Data, 0x3C, 2);
    public char RegionCode => (char)Data[0x3E];
    public byte Version => Data[0x3F];

    /// <summary>Game code as printed on the cart label, e.g. "NREE" for Resident Evil 2 (U).</summary>
    public string GameCode => Encoding.ASCII.GetString(Data, 0x3B, 4);

    /// <summary>Where this build keeps the tables and assets the tool looks up.</summary>
    public Re2Layout Layout => Re2Version.Detect(this).Layout;

    public CicChip Cic => N64Crc.DetectCic(Data);

    public uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset, 4));
    public ushort ReadU16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(offset, 2));
    public void WriteU32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Data.AsSpan(offset, 4), value);

    public ReadOnlySpan<byte> Slice(int offset, int length) => Data.AsSpan(offset, length);

    /// <summary>True when the header CRC pair matches the current contents.</summary>
    public bool VerifyCrc()
    {
        var cic = Cic;
        if (cic == CicChip.Unknown) return false;
        var (c1, c2) = N64Crc.Compute(Data, cic);
        return c1 == Crc1 && c2 == Crc2;
    }

    /// <summary>Recomputes and stores the header CRC pair. Call this after any edit below 0x101000.</summary>
    public void FixCrc()
    {
        var cic = Cic;
        if (cic == CicChip.Unknown)
            throw new InvalidOperationException("Cannot fix CRC: the CIC chip could not be identified from the boot code.");
        var (c1, c2) = N64Crc.Compute(Data, cic);
        Crc1 = c1;
        Crc2 = c2;
    }

    public void Save(string path, bool fixCrc = true)
    {
        if (fixCrc) FixCrc();
        File.WriteAllBytes(path, Data);
    }
}
