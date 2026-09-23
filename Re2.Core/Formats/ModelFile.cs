using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Re2.Core.Formats;

public sealed record ModelSection(int Index, int Offset, int Size)
{
    public int End => Offset + Size;
}

/// <summary>One entry of the table at the end of a model: an offset and a count.</summary>
public sealed record ModelClip(int Index, int Offset, int Count);

/// <summary>A character model asset. 143 assets (ids 1984..2126) share this layout.</summary>
public sealed class ModelFile
{
    public const int HeaderSize = 0x18;
    public const int SectionCount = 4;
    public const int PartTableSectionSize = 64;
    public const byte NoPart = 0xFF;
    private const uint ClipTerminator = 0xFFFFFFFF;

    public byte[] Data { get; }
    public IReadOnlyList<ModelSection> Sections { get; }
    public uint Field4 { get; }
    public uint Field5 { get; }

    /// <summary>Offsets of the geometry blocks, from the table at the head of section 1.</summary>
    public IReadOnlyList<int> GeometryBlockOffsets { get; }

    /// <summary>
    /// Section 2's per-part bytes, with the leading 0xFF marker and trailing padding removed.
    /// </summary>
    public IReadOnlyList<byte> PartIndices { get; }

    /// <summary>Section 3's (offset, count) pairs. Empty when the section is absent.</summary>
    public IReadOnlyList<ModelClip> Clips { get; }

    /// <summary>Number of parts, taken from section 2.</summary>
    public int PartCount => PartIndices.Count;

    /// <summary>Geometry blocks per part: 1 or 2. Zero when there are no parts.</summary>
    public int BlocksPerPart => PartCount == 0 ? 0 : (GeometryBlockOffsets.Count + PartCount - 1) / PartCount;

    private ModelFile(byte[] data, List<ModelSection> sections, uint field4, uint field5,
        List<int> geometryBlockOffsets, List<byte> partIndices, List<ModelClip> clips)
    {
        Data = data;
        Sections = sections;
        Field4 = field4;
        Field5 = field5;
        GeometryBlockOffsets = geometryBlockOffsets;
        PartIndices = partIndices;
        Clips = clips;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));

    /// <summary>Cheap check for the model layout, safe to run over every asset.</summary>
    public static bool LooksLikeModel(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + PartTableSectionSize) return false;
        if (U32(data, 0) != HeaderSize) return false;

        uint previous = HeaderSize;
        bool sawAbsent = false;

        for (int i = 1; i < SectionCount; i++)
        {
            uint offset = U32(data, i * 4);
            if (offset == 0) { sawAbsent = true; continue; }   // absent section
            if (sawAbsent) return false;                        // present sections must be contiguous
            if (offset <= previous || offset > data.Length) return false;
            previous = offset;
        }
        return true;
    }

    public static bool TryParse(byte[] data, out ModelFile model)
    {
        model = null!;
        if (!LooksLikeModel(data)) return false;
        model = Parse(data);
        return true;
    }

    public static ModelFile Parse(byte[] data)
    {
        if (!LooksLikeModel(data))
            throw new InvalidDataException("Not a model asset (expected a 0x18 header of ascending section offsets).");

        var offsets = new int[SectionCount];
        for (int i = 0; i < SectionCount; i++) offsets[i] = (int)U32(data, i * 4);

        var sections = new List<ModelSection>(SectionCount);
        for (int i = 0; i < SectionCount; i++)
        {
            if (offsets[i] == 0) { sections.Add(new ModelSection(i, 0, 0)); continue; }

            // A section runs to the next section that is actually present, or to end of file.
            int end = data.Length;
            for (int j = i + 1; j < SectionCount; j++)
                if (offsets[j] != 0) { end = offsets[j]; break; }

            sections.Add(new ModelSection(i, offsets[i], end - offsets[i]));
        }

        uint field4 = U32(data, 0x10);
        uint field5 = U32(data, 0x14);

        var geometryBlockOffsets = ReadGeometryBlockOffsets(data, sections[1]);
        var partIndices = ReadPartIndices(data, sections[2]);
        var clips = sections[3].Size > 0 ? ReadClips(data, sections[3]) : new List<ModelClip>();

        return new ModelFile(data, sections, field4, field5, geometryBlockOffsets, partIndices, clips);
    }

    /// <summary>
    /// Section 1 opens with one u32 per geometry block, immediately followed by the first block.
    /// </summary>
    private static List<int> ReadGeometryBlockOffsets(ReadOnlySpan<byte> data, ModelSection section)
    {
        var offsets = new List<int>();
        if (section.Size < 4) return offsets;

        int first = (int)U32(data, section.Offset);
        if (first <= section.Offset || first > section.End) return offsets;

        int count = (first - section.Offset) / 4;
        for (int i = 0; i < count; i++)
        {
            int at = section.Offset + i * 4;
            if (at + 4 > section.End) break;
            offsets.Add((int)U32(data, at));
        }

        return offsets;
    }

    /// <summary>
    /// Section 2 opens with a 0xFF marker, then one byte per part, then 0xFF padding to 64 bytes.
    /// </summary>
    private static List<byte> ReadPartIndices(ReadOnlySpan<byte> data, ModelSection section)
    {
        var indices = new List<byte>();
        int limit = Math.Min(section.Size, PartTableSectionSize);

        int start = 0;
        while (start < limit && data[section.Offset + start] == NoPart) start++;

        int end = limit;
        while (end > start && data[section.Offset + end - 1] == NoPart) end--;

        for (int i = start; i < end; i++) indices.Add(data[section.Offset + i]);
        return indices;
    }

    private static List<ModelClip> ReadClips(ReadOnlySpan<byte> data, ModelSection section)
    {
        var clips = new List<ModelClip>();
        for (int i = 0; section.Offset + (i + 1) * 8 <= section.End; i++)
        {
            int at = section.Offset + i * 8;
            uint offset = U32(data, at);
            if (offset == ClipTerminator) break;
            clips.Add(new ModelClip(clips.Count, (int)offset, (int)U32(data, at + 4)));
        }
        return clips;
    }

    /// <summary>Raw bytes of a section, for tooling that wants to dump or diff them.</summary>
    public ReadOnlySpan<byte> SectionData(int index)
    {
        var section = Sections[index];
        return Data.AsSpan(section.Offset, section.Size);
    }

    public override string ToString() =>
        $"{Data.Length:N0}B, {PartCount} parts, {GeometryBlockOffsets.Count} blocks, {Clips.Count} clips";
}
