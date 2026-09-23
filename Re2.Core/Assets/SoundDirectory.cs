using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>One entry of the sample directory.</summary>
public sealed record SoundSample(
    int Index,
    int Offset,
    int StoredSize,
    int SampleRate,
    uint DeclaredLength,
    uint LoopStart,
    uint LoopEnd)
{
    /// <summary>
    /// Stored bits per declared sample: 5 by construction (40 bytes per 64 samples), plus the
    /// codebook and the trailing slack, which is why it measures a little above 5.
    /// </summary>
    public double BitsPerSample => DeclaredLength == 0 ? 0 : StoredSize * 8.0 / DeclaredLength;

    /// <summary>Playback length in seconds, from the declared sample count and rate.</summary>
    public double Seconds => SampleRate == 0 ? 0 : (double)DeclaredLength / SampleRate;

    public bool IsLooping => LoopEnd > LoopStart;

    public override string ToString() => $"#{Index} {SampleRate}Hz {StoredSize:N0}B {Seconds:0.00}s";
}

/// <summary>The game's sound bank.</summary>
public sealed class SoundDirectory
{
    public const int SequenceAssetId = 1979;
    public const int ProjectAssetId = 1980;
    public const int PoolAssetId = 1981;
    public const int SampleDirectoryAssetId = 1982;
    public const int SampleDataAssetId = 1983;

    public const int RecordSize = 28;

    /// <summary>Constant occupying the high half of the rate word.</summary>
    public const ushort RateWordMarker = 0x3C00;

    /// <summary>Constant occupying the top byte of the length word.</summary>
    public const uint LengthWordMarker = 0x03;

    /// <summary>An FFFFFFFF word follows the last record, so the table is 28n + 4 bytes.</summary>
    public const int TerminatorSize = 4;

    public IReadOnlyList<SoundSample> Samples { get; }

    /// <summary>The packed sample blob (asset 1983), still encoded.</summary>
    public byte[] SampleData { get; }

    private SoundDirectory(List<SoundSample> samples, byte[] sampleData)
    {
        Samples = samples;
        SampleData = sampleData;
    }

    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.Slice(o, 4));

    public static SoundDirectory Read(RomFile rom, AssetDirectory directory)
    {
        byte[] Load(int id)
        {
            var entry = directory.Entries.FirstOrDefault(e => e.Index == id)
                ?? throw new InvalidDataException($"Sound asset {id} is not in the directory.");
            if (!directory.TryGetData(rom, entry, out var data))
                throw new InvalidDataException($"Sound asset {id} would not decode.");
            return data;
        }

        return ReadFrom(Load(SampleDirectoryAssetId), Load(SampleDataAssetId));
    }

    /// <summary>
    /// Parses a bank from loose directory and sample-data blobs, which is what lets a rebuilt bank
    /// be checked without going back through a ROM.
    /// </summary>
    public static SoundDirectory ReadFrom(byte[] table, byte[] blob)
    {
        int count = table.Length / RecordSize;
        var raw = new List<(int Index, int Offset, int Rate, uint Length, uint LoopStart, uint LoopEnd)>(count);

        for (int i = 0; i < count; i++)
        {
            int at = i * RecordSize;
            raw.Add((
                (int)(U32(table, at) >> 16),
                (int)U32(table, at + 4),
                (int)(U32(table, at + 12) & 0xFFFF),
                U32(table, at + 16) & 0x00FFFFFF,
                U32(table, at + 20),
                U32(table, at + 24)));
        }

        var samples = new List<SoundSample>(count);
        for (int i = 0; i < raw.Count; i++)
        {
            // A sample runs to wherever the next one starts; the last runs to the end of the blob.
            int end = i + 1 < raw.Count ? raw[i + 1].Offset : blob.Length;
            int size = Math.Max(0, end - raw[i].Offset);

            samples.Add(new SoundSample(raw[i].Index, raw[i].Offset, size, raw[i].Rate,
                raw[i].Length, raw[i].LoopStart, raw[i].LoopEnd));
        }

        return new SoundDirectory(samples, blob);
    }

    /// <summary>Raw, still-encoded bytes for one sample: codebook followed by frames.</summary>
    public ReadOnlySpan<byte> GetEncoded(SoundSample sample)
        => sample.Offset + sample.StoredSize <= SampleData.Length
            ? SampleData.AsSpan(sample.Offset, sample.StoredSize)
            : ReadOnlySpan<byte>.Empty;

    /// <summary>Decodes one sample to mono 16-bit PCM, trimmed to its declared length.</summary>
    public short[] Decode(SoundSample sample)
        => SoundCodec.Decode(GetEncoded(sample), (int)sample.DeclaredLength);

    /// <summary>Decodes one sample straight to a mono WAV at its own rate.</summary>
    public byte[] ToWav(SoundSample sample)
        => WavCodec.Write(Decode(sample), sample.SampleRate);
}
