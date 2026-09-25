using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>One speech clip in the voice bank.</summary>
public sealed record VoiceClip(
    int Index,
    int Offset,
    int StoredSize,
    int SampleRate,
    int BlockCount)
{
    /// <summary>Samples one decoder call produces.</summary>
    public const int SamplesPerBlock = 160;

    public int SampleCount => BlockCount * SamplesPerBlock;

    public double Seconds => SampleRate == 0 ? 0 : (double)SampleCount / SampleRate;
}

/// <summary>The game's voice bank: the cutscene and event dialogue.</summary>
public static class VoiceBank
{
    /// <summary>Rev 1's id; use <see cref="Re2Layout.VoiceBankAsset"/> for the ROM at hand.</summary>
    public const int AssetId = SoundDirectory.SequenceAssetId;

    public static readonly byte[] Magic = { (byte)'M', (byte)'O', (byte)'R', (byte)'T' };

    public const int HeaderSize = 16;

    /// <summary>Rates the entry's flag byte selects, as the player reads it.</summary>
    public const int LowRate = 8000, HighRate = 16000;

    public static IReadOnlyList<VoiceClip> Read(RomFile rom, AssetDirectory directory)
    {
        foreach (var entry in directory.Entries)
            if (entry.Index == rom.Layout.VoiceBankAsset && directory.TryGetData(rom, entry, out var data))
                return Read(data);

        return Array.Empty<VoiceClip>();
    }

    public static IReadOnlyList<VoiceClip> Read(ReadOnlySpan<byte> bank)
    {
        var clips = new List<VoiceClip>();
        if (bank.Length < 4) return clips;

        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(bank);
        if (count <= 0 || 4 + (long)count * 4 > bank.Length) return clips;

        for (int i = 0; i < count; i++)
        {
            uint entry = BinaryPrimitives.ReadUInt32BigEndian(bank[(4 + i * 4)..]);
            int offset = (int)(entry & 0xFFFFFF);
            int rate = (entry >> 24) == 0 ? LowRate : HighRate;

            if (offset < 0 || offset + HeaderSize > bank.Length) continue;
            if (!bank.Slice(offset, 4).SequenceEqual(Magic)) continue;

            // A clip runs to the next one, and the last to the end of the bank.
            int next = i + 1 < count
                ? (int)(BinaryPrimitives.ReadUInt32BigEndian(bank[(4 + (i + 1) * 4)..]) & 0xFFFFFF)
                : bank.Length;

            int size = Math.Clamp(next - offset, 0, bank.Length - offset);
            int blocks = BinaryPrimitives.ReadUInt16BigEndian(bank[(offset + 4)..]);

            clips.Add(new VoiceClip(i, offset, size, rate, blocks));
        }

        return clips;
    }

    /// <summary>
    /// Fits a replacement into a clip's existing slot, leaving every other clip exactly where it is.
    /// </summary>
    public static byte[] ReplaceInPlace(ReadOnlySpan<byte> bank, VoiceClip clip,
                                        ReadOnlySpan<byte> replacement, Formats.MortCodec.Padder pad)
    {
        if (replacement.Length < HeaderSize || !replacement[..4].SequenceEqual(Magic))
            throw new InvalidDataException("A replacement clip has to start with the MORT header.");

        int blocks = Formats.MortCodec.ReadBlockCount(replacement);
        int rate = Formats.MortCodec.ReadSampleRate(replacement);

        if (blocks > clip.BlockCount)
        {
            double over = (blocks - clip.BlockCount) * (double)VoiceClip.SamplesPerBlock / Math.Max(1, rate);
            throw new InvalidDataException(
                $"This clip is too long for slot {clip.Index}. The slot holds {clip.Seconds:0.00}s and " +
                $"the replacement runs {blocks * (double)VoiceClip.SamplesPerBlock / Math.Max(1, rate):0.00}s -- " +
                $"{over:0.00}s over. Dialogue is timed to the action in its scene, so it cannot run " +
                "past the original. Shorten it and try again.");
        }

        var fitted = blocks < clip.BlockCount ? pad(replacement, blocks, clip.BlockCount) : replacement.ToArray();

        if (fitted.Length > clip.StoredSize)
        {
            throw new InvalidDataException(
                $"This clip does not fit slot {clip.Index}: it encodes to {fitted.Length:N0} bytes and the " +
                $"slot holds {clip.StoredSize:N0}. It is the right length, but the original was stored more " +
                "compactly. Replacing it would mean moving every clip after it.");
        }

        var result = bank.ToArray();
        Array.Clear(result, clip.Offset, clip.StoredSize);
        fitted.CopyTo(result.AsSpan(clip.Offset));

        // The slot's length is unchanged, so the header has to keep describing the slot rather than
        // the replacement, or the next clip's start would be lost.
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(clip.Offset + 8), (uint)(clip.StoredSize / 4));

        // The rate flag travels with the audio.
        int entry = 4 + clip.Index * 4;
        uint flag = rate == LowRate ? 0u : 1u;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry), (flag << 24) | (uint)clip.Offset);

        return result;
    }

    /// <summary>The size the clip's own header declares, for checking against the table's spacing.</summary>
    public static int DeclaredSize(ReadOnlySpan<byte> bank, VoiceClip clip)
        => (int)BinaryPrimitives.ReadUInt32BigEndian(bank[(clip.Offset + 8)..]) * 4;
}
