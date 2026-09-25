using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Emulation;
using Re2.Core.Rom;

namespace Re2.Core.Formats;

/// <summary>
/// Decodes the "MORT" speech codec the voice bank is stored in, by running the game's own decoder.
/// </summary>
public static class MortCodec
{
    /// <summary>One block of decoded audio, fixed by the decoder.</summary>
    public const int SamplesPerBlock = 160;
    public const int BytesPerBlock = SamplesPerBlock * 2;

    /// <summary>The per-block decode entry point in the main overlay.</summary>
    public const uint DecodeBlock = 0x800B53F4;

    /// <summary>Where the streamed clip data sits inside the decoder's context, and how much of it.</summary>
    private const uint RingOffset = 0x1A0;
    private const int RingSize = 0x1000;
    private const int ChunkSize = 0x400;

    /// <summary>Context fields the game's own setup writes before the first block.</summary>
    private const uint FrameSizeField = 0x166;
    private const uint BitPositionField = 0x11C8;

    private const uint ContextSize = 0x1228;

    private const uint ContextAt = 0x80200000;
    private const uint OutputAt = 0x80220000;
    private const uint StackTop = 0x80300000;

    /// <summary>The bit position the game starts a clip at: 0x60, twelve bytes in.</summary>
    public const int FirstBitPosition = 0x60;

    /// <summary>Bits the decoder reads for a run selector, and for each kind of run length.</summary>
    public const int SelectorBits = 1, NormalRunBits = 7, SilentRunBits = 4;

    /// <summary>Longest run each kind can express; the length is stored less one.</summary>
    public const int MaxNormalRun = 1 << NormalRunBits, MaxSilentRun = 1 << SilentRunBits;

    /// <summary>Builds a clip of nothing but silence.</summary>
    public static byte[] Silence(int blockCount, int sampleRate)
    {
        var writer = new MortBitWriter((int)FirstBitPosition);

        for (int written = 0; written < blockCount;)
        {
            int run = Math.Min(MaxSilentRun, blockCount - written);
            writer.Write(1, SelectorBits);                 // this run is silent
            writer.Write(run - 1, SilentRunBits);
            written += run;
        }

        var body = writer.ToArray();
        var clip = new byte[Math.Max(body.Length, Assets.VoiceBank.HeaderSize)];
        body.CopyTo(clip, 0);

        WriteHeader(clip, blockCount, sampleRate);
        return clip;
    }

    /// <summary>Bit widths of a normal block's eight scale fields, in the order the decoder reads them.</summary>
    public static readonly int[] ScaleFieldBits = { 6, 6, 5, 5, 4, 4, 3, 3 };

    /// <summary>Bit widths of the four side values that open each of a block's four groups.</summary>
    public static readonly int[] GroupSideBits = { 7, 2, 2, 6 };

    public const int CoefficientsPerGroup = 13, CoefficientBits = 3, GroupsPerBlock = 4;

    /// <summary>
    /// A normal block is a fixed 260 bits: eight scale fields, then four groups of four side values and
    /// thirteen three-bit coefficients.
    /// </summary>
    public const int NormalBlockBits = 260;

    /// <summary>One decoded block's worth of fields, as the decoder reads them.</summary>
    public sealed record Block(int[] Scales, Group[] Groups)
    {
        public static Block Zero() => new(new int[8],
            Enumerable.Range(0, GroupsPerBlock).Select(_ => Group.Zero()).ToArray());
    }

    public sealed record Group(int[] Side, int[] Coefficients)
    {
        public static Group Zero() => new(new int[4], new int[CoefficientsPerGroup]);
    }

    /// <summary>Writes a clip made of normal blocks with the given fields.</summary>
    public static byte[] WriteBlocks(IReadOnlyList<Block> blocks, int sampleRate)
    {
        var writer = new MortBitWriter((int)FirstBitPosition);

        for (int written = 0; written < blocks.Count;)
        {
            int run = Math.Min(MaxNormalRun, blocks.Count - written);
            writer.Write(0, SelectorBits);                 // this run is normal blocks
            writer.Write(run - 1, NormalRunBits);

            for (int i = 0; i < run; i++)
            {
                var block = blocks[written + i];

                for (int f = 0; f < ScaleFieldBits.Length; f++)
                    writer.Write(block.Scales[f], ScaleFieldBits[f]);

                foreach (var group in block.Groups)
                {
                    for (int f = 0; f < GroupSideBits.Length; f++)
                        writer.Write(group.Side[f], GroupSideBits[f]);

                    foreach (int coefficient in group.Coefficients)
                        writer.Write(coefficient, CoefficientBits);
                }
            }

            written += run;
        }

        var body = writer.ToArray();
        var clip = new byte[Math.Max(body.Length, Assets.VoiceBank.HeaderSize)];
        body.CopyTo(clip, 0);

        WriteHeader(clip, blocks.Count, sampleRate);
        return clip;
    }

    /// <summary>
    /// Writes a clip whose blocks may be either normal or silent, packing each kind into the run
    /// lengths its selector allows.
    /// </summary>
    public static byte[] WriteMixed(IReadOnlyList<Block?> blocks, int sampleRate)
    {
        var writer = new MortBitWriter((int)FirstBitPosition);

        for (int at = 0; at < blocks.Count;)
        {
            bool silent = blocks[at] is null;

            int run = 0;
            int limit = silent ? MaxSilentRun : MaxNormalRun;
            while (at + run < blocks.Count && run < limit && (blocks[at + run] is null) == silent) run++;

            writer.Write(silent ? 1 : 0, SelectorBits);
            writer.Write(run - 1, silent ? SilentRunBits : NormalRunBits);

            if (!silent)
                for (int i = 0; i < run; i++)
                {
                    var block = blocks[at + i]!;

                    for (int f = 0; f < ScaleFieldBits.Length; f++)
                        writer.Write(block.Scales[f], ScaleFieldBits[f]);

                    foreach (var group in block.Groups)
                    {
                        for (int f = 0; f < GroupSideBits.Length; f++)
                            writer.Write(group.Side[f], GroupSideBits[f]);

                        foreach (int coefficient in group.Coefficients)
                            writer.Write(coefficient, CoefficientBits);
                    }
                }

            at += run;
        }

        var body = writer.ToArray();
        var clip = new byte[Math.Max(body.Length, Assets.VoiceBank.HeaderSize)];
        body.CopyTo(clip, 0);

        WriteHeader(clip, blocks.Count, sampleRate);
        return clip;
    }

    /// <summary>Pads a clip out to a longer block count.</summary>
    public delegate byte[] Padder(ReadOnlySpan<byte> clip, int blockCount, int targetBlocks);

    /// <summary>Lengthens a clip to <paramref name="targetBlocks"/> by appending silence.</summary>
    public static byte[] PadWithSilence(MipsCpu cpu, ReadOnlySpan<byte> clip, int blockCount, int targetBlocks)
    {
        if (targetBlocks < blockCount)
            throw new ArgumentOutOfRangeException(nameof(targetBlocks), "A clip cannot be padded to be shorter.");
        if (targetBlocks == blockCount) return clip.ToArray();

        Decode(cpu, clip, blockCount, out int end);

        var writer = new MortBitWriter(clip, end);

        for (int written = blockCount; written < targetBlocks;)
        {
            int run = Math.Min(MaxSilentRun, targetBlocks - written);
            writer.Write(1, SelectorBits);
            writer.Write(run - 1, SilentRunBits);
            written += run;
        }

        var padded = writer.ToArray();
        WriteHeader(padded, targetBlocks, ReadSampleRate(clip));
        return padded;
    }

    public static int ReadBlockCount(ReadOnlySpan<byte> clip)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(clip[4..]);

    public static int ReadSampleRate(ReadOnlySpan<byte> clip)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(clip[6..]);

    /// <summary>Stamps the 16-byte clip header.</summary>
    public static void WriteHeader(Span<byte> clip, int blockCount, int sampleRate)
    {
        Assets.VoiceBank.Magic.CopyTo(clip);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(clip[4..], (ushort)blockCount);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(clip[6..], (ushort)sampleRate);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(clip[8..], (uint)(clip.Length / 4));
    }

    public static short[] Decode(RomFile rom, ReadOnlySpan<byte> clip, int blockCount)
        => Decode(GameCode.Load(rom), clip, blockCount);

    public static short[] Decode(MipsCpu cpu, ReadOnlySpan<byte> clip, int blockCount)
        => Decode(cpu, clip, blockCount, out _);

    public static short[] Decode(MipsCpu cpu, ReadOnlySpan<byte> clip, int blockCount,
                                 out int endBitPosition)
    {
        endBitPosition = (int)FirstBitPosition;
        if (blockCount <= 0) return Array.Empty<short>();

        // A fresh context per clip: the decoder carries run counters and a bit position across
        // blocks, so anything left from a previous clip would be read as this one's state.
        for (uint i = 0; i < ContextSize; i += 4) cpu.Write32(ContextAt + i, 0);

        cpu.Write16(ContextAt + FrameSizeField, 0x28);
        cpu.Write32(ContextAt + BitPositionField, FirstBitPosition);

        // Which clip chunk is currently in each ring slot; -1 until filled.
        var resident = new int[RingSize / ChunkSize];
        Array.Fill(resident, -1);

        var samples = new short[blockCount * SamplesPerBlock];

        for (int block = 0; block < blockCount; block++)
        {
            uint bitPosition = cpu.Read32(ContextAt + BitPositionField);
            FillRing(cpu, clip, resident, (int)(bitPosition >> 3));

            cpu.Reg[4] = ContextAt;
            cpu.Reg[5] = OutputAt;
            cpu.Reg[29] = StackTop;
            cpu.Call(cpu.Layout.Address(DecodeBlock));

            for (int i = 0; i < SamplesPerBlock; i++)
                samples[block * SamplesPerBlock + i] = (short)cpu.Read16(OutputAt + (uint)i * 2);
        }

        endBitPosition = (int)cpu.Read32(ContextAt + BitPositionField);
        return samples;
    }

    /// <summary>Keeps the chunks the decoder is about to read resident in the ring.</summary>
    private static void FillRing(MipsCpu cpu, ReadOnlySpan<byte> clip, int[] resident, int bytePosition)
    {
        int first = bytePosition / ChunkSize;

        for (int chunk = first; chunk <= first + 2; chunk++)
        {
            int slot = chunk % resident.Length;
            if (resident[slot] == chunk) continue;

            uint at = ContextAt + RingOffset + (uint)(slot * ChunkSize);
            int from = chunk * ChunkSize;

            for (int i = 0; i < ChunkSize; i++)
            {
                int source = from + i;
                cpu.Write8(at + (uint)i, source < clip.Length ? clip[source] : (byte)0);
            }

            resident[slot] = chunk;
        }
    }
}
