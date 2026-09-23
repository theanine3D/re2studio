using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Re2.Core.Formats;

/// <summary>An F3DEX2 vertex: position, texture coordinate, and either a colour or a normal.</summary>
public readonly record struct MeshVertex(short X, short Y, short Z, short S, short T, byte R, byte G, byte B, byte A)
{
    /// <summary>Texture coordinates, normalised to 0..1.</summary>
    public const float UvScale = 32f * 512f;

    public float U => S / UvScale;
    public float V => T / UvScale;

    /// <summary>Interprets the colour bytes as a signed unit normal, which is how lit meshes use them.</summary>
    public (float X, float Y, float Z) AsNormal()
    {
        static float S8(byte b) => (sbyte)b / 127f;
        return (S8(R), S8(G), S8(B));
    }
}

public readonly record struct MeshTriangle(int A, int B, int C);

/// <summary>One display list plus the vertex block it draws from.</summary>
public sealed class SubMesh
{
    public int Index { get; init; }
    public int VertexOffset { get; init; }
    public int DisplayListOffset { get; init; }
    public int MaterialOffset { get; init; }
    public int VertexCount { get; init; }

    /// <summary>Index into the model's texture list, or -1 for untextured geometry.</summary>
    public int TextureIndex { get; init; } = -1;

    public bool HasTexture => TextureIndex >= 0;

    public uint Colour0 { get; init; }
    public uint Colour1 { get; init; }

    /// <summary>True when this came from the part's second geometry list.</summary>
    public bool IsSecondary { get; init; }
    public List<MeshVertex> Vertices { get; } = new();
    public List<MeshTriangle> Triangles { get; } = new();
}

public sealed class MeshPart
{
    public int Index { get; init; }
    public int SubMeshCount { get; init; }
    public int Offset { get; init; }
    public int SecondaryOffset { get; init; }
    public List<SubMesh> SubMeshes { get; } = new();
}

/// <summary>A character mesh.</summary>
public sealed class MeshFile
{
    public const ushort VersionId = 0x0141;
    public const int HeaderSize = 0x20;
    public const int VertexSize = 16;
    public const int SubMeshRecordSize = 16;
    public const int PartRecordSize = 16;

    // F3DEX2 opcodes
    private const byte GVtx = 0x01;
    private const byte GTri1 = 0x05;
    private const byte GTri2 = 0x06;
    private const byte GEndDl = 0xDF;

    /// <summary>Guard against a malformed list running away.</summary>
    private const int MaxDisplayListCommands = 4096;

    /// <summary>F3DEX2's vertex buffer.</summary>
    public const int VertexBufferSlots = 64;

    public int Version { get; private set; }
    public int PartCount { get; private set; }
    public int PartTableOffset { get; private set; }

    /// <summary>How many textures the model references; matches the pair table's record count.</summary>
    public int TextureCount { get; private set; }
    public List<MeshPart> Parts { get; } = new();

    public int TotalVertices { get { int n = 0; foreach (var p in Parts) foreach (var s in p.SubMeshes) n += s.Vertices.Count; return n; } }
    public int TotalTriangles { get { int n = 0; foreach (var p in Parts) foreach (var s in p.SubMeshes) n += s.Triangles.Count; return n; } }

    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.Slice(o, 4));
    private static short I16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt16BigEndian(d.Slice(o, 2));

    public static bool LooksLikeMesh(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(data[..2]) != VersionId) return false;

        int partTable = (int)U32(data, 8);
        int partCount = (int)U32(data, 4);
        if (partCount is <= 0 or > 256) return false;

        return partTable > 0 && partTable + partCount * PartRecordSize <= data.Length;
    }

    /// <summary>Probe form, safe to run across every asset in the ROM.</summary>
    public static bool TryParse(byte[] data, out MeshFile mesh)
    {
        mesh = null!;
        if (!LooksLikeMesh(data)) return false;

        try { mesh = Parse(data); }
        catch (Exception) { return false; }

        return true;
    }

    public static MeshFile Parse(byte[] data)
    {
        if (!LooksLikeMesh(data)) throw new InvalidDataException("Not a mesh asset (bad version or part table).");

        var mesh = new MeshFile
        {
            Version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)),
            PartTableOffset = (int)U32(data, 8),
            PartCount = (int)U32(data, 4),
            TextureCount = (int)U32(data, 20)
        };

        for (int i = 0; i < mesh.PartCount; i++)
        {
            int at = mesh.PartTableOffset + i * PartRecordSize;
            int subCount = (int)U32(data, at);
            int offset = (int)U32(data, at + 4);
            int secondary = (int)U32(data, at + 8);

            if (subCount < 0 || subCount > 4096) subCount = 0;
            var part = new MeshPart { Index = i, SubMeshCount = subCount, Offset = offset, SecondaryOffset = secondary };
            mesh.Parts.Add(part);

            // Empty slots carry a zero offset; the character simply has no geometry for that part.
            if (subCount <= 0) continue;

            ReadGeometryList(data, part, offset, subCount, secondary: false);
            ReadGeometryList(data, part, secondary, subCount, secondary: true);
        }

        return mesh;
    }

    private static void ReadGeometryList(ReadOnlySpan<byte> data, MeshPart part, int offset, int subCount, bool secondary)
    {
        if (offset <= 0) return;

        for (int s = 0; s < subCount; s++)
        {
            int rec = offset + s * SubMeshRecordSize;
            if (rec < 0 || rec + SubMeshRecordSize > data.Length) break;

            int material = (int)U32(data, rec + 8);
            int textureIndex = -1;
            uint colour0 = 0, colour1 = 0;

            if (material >= 0 && material + 16 <= data.Length)
            {
                colour0 = U32(data, material);
                colour1 = U32(data, material + 4);
                textureIndex = (int)U32(data, material + 8);
            }

            var sub = new SubMesh
            {
                Index = s,
                VertexOffset = (int)U32(data, rec),
                DisplayListOffset = (int)U32(data, rec + 4),
                MaterialOffset = material,
                VertexCount = Math.Clamp((int)(U32(data, rec + 12) & 0xFFFF), 0, 4096),
                TextureIndex = textureIndex,
                Colour0 = colour0,
                Colour1 = colour1,
                IsSecondary = secondary
            };

            ReadVertices(data, sub);
            RunDisplayList(data, sub);
            part.SubMeshes.Add(sub);
        }
    }

    private static void ReadVertices(ReadOnlySpan<byte> data, SubMesh sub)
    {
        for (int i = 0; i < sub.VertexCount; i++)
        {
            int at = sub.VertexOffset + i * VertexSize;
            if (at < 0 || at + VertexSize > data.Length) break;

            sub.Vertices.Add(new MeshVertex(
                I16(data, at), I16(data, at + 2), I16(data, at + 4),
                I16(data, at + 8), I16(data, at + 10),
                data[at + 12], data[at + 13], data[at + 14], data[at + 15]));
        }
    }

    /// <summary>
    /// Walks an F3DEX2 display list, emulating the vertex buffer so triangle indices resolve to the
    /// right vertices.
    /// </summary>
    private static void RunDisplayList(ReadOnlySpan<byte> data, SubMesh sub)
    {
        int at = sub.DisplayListOffset;
        if (at < 0) return;

        // Buffer slot -> index into the sub-mesh's vertex block; -1 while a slot is unloaded.
        var slots = new int[VertexBufferSlots];
        Array.Fill(slots, -1);

        for (int i = 0; i < MaxDisplayListCommands; i++)
        {
            if (at < 0 || at + 8 > data.Length) return;

            byte op = data[at];
            uint w0 = U32(data, at);
            uint w1 = U32(data, at + 4);
            at += 8;

            switch (op)
            {
                case GEndDl:
                    return;

                case GVtx:
                {
                    // gsSPVertex packs the count at bit 12 and (v0 + n) << 1 in the low bits; the
                    // address is a byte offset into this sub-mesh's own vertex block.
                    int count = (int)((w0 >> 12) & 0xFF);
                    int destination = (int)((w0 & 0xFFF) >> 1) - count;
                    int firstVertex = (int)(w1 & 0xFFFFFF) / VertexSize;

                    for (int k = 0; k < count; k++)
                    {
                        int slot = destination + k;
                        if (slot >= 0 && slot < slots.Length) slots[slot] = firstVertex + k;
                    }
                    break;
                }

                case GTri1:
                    AddTriangle(sub, slots, w0 >> 16, w0 >> 8, w0);
                    break;

                case GTri2:
                    AddTriangle(sub, slots, w0 >> 16, w0 >> 8, w0);
                    AddTriangle(sub, slots, w1 >> 16, w1 >> 8, w1);
                    break;
            }
        }
    }

    /// <summary>F3DEX2 stores buffer slots pre-multiplied by two.</summary>
    private static void AddTriangle(SubMesh sub, int[] slots, uint a, uint b, uint c)
    {
        int sa = (int)(a & 0xFF) / 2, sb = (int)(b & 0xFF) / 2, sc = (int)(c & 0xFF) / 2;
        if (sa >= slots.Length || sb >= slots.Length || sc >= slots.Length) return;

        int ia = slots[sa], ib = slots[sb], ic = slots[sc];
        int n = sub.Vertices.Count;
        if (ia >= 0 && ib >= 0 && ic >= 0 && ia < n && ib < n && ic < n)
            sub.Triangles.Add(new MeshTriangle(ia, ib, ic));
    }
}
