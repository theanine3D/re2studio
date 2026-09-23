using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Re2.Core.Formats;

/// <summary>One sub-mesh being built: a vertex pool, its triangles, and its material.</summary>
public sealed class MeshBuildSubMesh
{
    public List<MeshVertex> Vertices { get; } = new();
    public List<MeshTriangle> Triangles { get; } = new();

    /// <summary>Index into the model's texture list, or -1 for untextured (stored as 0xFFFFFFFF).</summary>
    public int TextureIndex { get; set; } = -1;

    /// <summary>Defaults are the values the retail models use almost everywhere.</summary>
    public uint Colour0 { get; set; } = 0xB2B2B2FF;
    public uint Colour1 { get; set; } = 0x7F7F7FFF;
}

/// <summary>One part, i.e. one skeleton joint's geometry.</summary>
public sealed class MeshBuildPart
{
    public List<MeshBuildSubMesh> SubMeshes { get; } = new();

    /// <summary>The part's second geometry list, used by 36 of the 337 retail models.</summary>
    public List<MeshBuildSubMesh> SecondarySubMeshes { get; } = new();
}

/// <summary>A whole model, ready to encode.</summary>
public sealed class MeshBuildModel
{
    /// <summary>Indexed by part number; empty entries are written as empty parts.</summary>
    public List<MeshBuildPart> Parts { get; } = new();

    /// <summary>How many textures the model references. Kept from the mesh being replaced.</summary>
    public int TextureCount { get; set; }

    public int TotalVertices => Parts.Sum(p => p.SubMeshes.Concat(p.SecondarySubMeshes).Sum(s => s.Vertices.Count));
    public int TotalTriangles => Parts.Sum(p => p.SubMeshes.Concat(p.SecondarySubMeshes).Sum(s => s.Triangles.Count));

    /// <summary>
    /// Copies a parsed mesh into buildable form, so a model can be re-encoded unchanged.
    /// </summary>
    public static MeshBuildModel FromMeshFile(MeshFile mesh)
    {
        var model = new MeshBuildModel { TextureCount = mesh.TextureCount };

        foreach (var part in mesh.Parts)
        {
            var built = new MeshBuildPart();
            foreach (var source in part.SubMeshes)
            {
                var target = new MeshBuildSubMesh
                {
                    TextureIndex = source.TextureIndex,
                    Colour0 = source.Colour0,
                    Colour1 = source.Colour1
                };
                target.Vertices.AddRange(source.Vertices);
                target.Triangles.AddRange(source.Triangles);
                (source.IsSecondary ? built.SecondarySubMeshes : built.SubMeshes).Add(target);
            }
            model.Parts.Add(built);
        }

        return model;
    }
}

/// <summary>
/// Encodes a <see cref="MeshBuildModel"/> into the game's mesh asset format -- the write side of <see
/// cref="MeshFile"/>.
/// </summary>
public static class MeshWriter
{
    public const int HeaderBytes = 0x18;
    public const int MaterialBytes = 16;

    /// <summary>Vertices one G_VTX can load, and therefore the size of a chunk.</summary>
    public const int MaxVerticesPerLoad = MeshFile.VertexBufferSlots;

    /// <summary>Sanity ceiling on a single sub-mesh; the largest retail one holds 855.</summary>
    public const int MaxVerticesPerSubMesh = 8192;

    private const uint SegmentTwoBase = 0x02000000;
    private const byte GVtx = 0x01, GTri1 = 0x05, GTri2 = 0x06, GEndDl = 0xDF;

    public static byte[] Write(MeshBuildModel model)
    {
        Validate(model);

        var output = new List<byte>(1 << 16);
        output.AddRange(new byte[HeaderBytes]);

        var primaryRecords = new int[model.Parts.Count];
        var secondaryRecords = new int[model.Parts.Count];

        for (int p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p];
            if (part.SubMeshes.Count == 0) continue;

            primaryRecords[p] = EmitGeometryList(output, part.SubMeshes);
            secondaryRecords[p] = part.SecondarySubMeshes.Count > 0
                ? EmitGeometryList(output, part.SecondarySubMeshes)
                : 0;
        }

        int partTableAt = output.Count;
        for (int p = 0; p < model.Parts.Count; p++)
        {
            AddU32(output, (uint)model.Parts[p].SubMeshes.Count);
            AddU32(output, (uint)primaryRecords[p]);
            AddU32(output, (uint)secondaryRecords[p]);
            AddU32(output, 0);          // no extra block
        }

        var data = output.ToArray();
        var header = data.AsSpan(0, HeaderBytes);
        BinaryPrimitives.WriteUInt16BigEndian(header, MeshFile.VersionId);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0);                    // not yet relocated
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)model.Parts.Count);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)partTableAt);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(header[20..], (uint)model.TextureCount);

        return data;
    }

    /// <summary>
    /// Writes one list's sub-mesh payloads followed by its records, and returns where the records
    /// start.
    /// </summary>
    private static int EmitGeometryList(List<byte> output, List<MeshBuildSubMesh> subMeshes)
    {
        var records = new List<(int Vertex, int DisplayList, int Material, int Count)>(subMeshes.Count);

        foreach (var sub in subMeshes)
        {
            // Chunking has to happen before anything is written: the vertex block is laid out as the
            // chunks in order, so each G_VTX can load a contiguous run.
            var chunks = MeshSplitter.Split(sub.Vertices, sub.Triangles, sub.TextureIndex,
                                            sub.Colour0, sub.Colour1, MaxVerticesPerLoad);

            int materialAt = output.Count;
            WriteMaterial(output, sub);

            int vertexAt = output.Count;
            foreach (var chunk in chunks)
                foreach (var v in chunk.Vertices)
                    WriteVertex(output, v);

            int displayListAt = output.Count;
            WriteDisplayList(output, chunks);

            int written = chunks.Sum(c => c.Vertices.Count);
            records.Add((vertexAt, displayListAt, materialAt, written));
        }

        int recordsAt = output.Count;
        foreach (var (vertex, displayList, material, count) in records)
        {
            AddU32(output, (uint)vertex);
            AddU32(output, (uint)displayList);
            AddU32(output, (uint)material);
            AddU32(output, (uint)count);
        }

        return recordsAt;
    }

    // ---- pieces -----------------------------------------------------------

    private static void WriteMaterial(List<byte> output, MeshBuildSubMesh sub)
    {
        AddU32(output, sub.Colour0);
        AddU32(output, sub.Colour1);
        AddU32(output, sub.TextureIndex < 0 ? 0xFFFFFFFF : (uint)sub.TextureIndex);
        AddU32(output, 0);
    }

    private static void WriteVertex(List<byte> output, MeshVertex v)
    {
        AddI16(output, v.X); AddI16(output, v.Y); AddI16(output, v.Z);
        AddI16(output, 0);                       // the F3DEX2 flag word, always zero in retail data
        AddI16(output, v.S); AddI16(output, v.T);
        output.Add(v.R); output.Add(v.G); output.Add(v.B); output.Add(v.A);
    }

    /// <summary>
    /// One G_VTX per chunk, loading it at buffer slot 0 from its own offset in the vertex block,
    /// followed by that chunk's triangles. Indices are already chunk-local.
    /// </summary>
    private static void WriteDisplayList(List<byte> output, List<MeshBuildSubMesh> chunks)
    {
        int firstVertex = 0;

        foreach (var chunk in chunks)
        {
            int n = chunk.Vertices.Count;

            // gsSPVertex(seg2 + firstVertex*16, n, 0)
            AddU32(output, (uint)((GVtx << 24) | (n << 12) | (n << 1)));
            AddU32(output, SegmentTwoBase + (uint)(firstVertex * MeshFile.VertexSize));

            var tris = chunk.Triangles;
            int i = 0;
            for (; i + 1 < tris.Count; i += 2)
            {
                AddU32(output, (GTri2 << 24) | Packed(tris[i]));
                AddU32(output, Packed(tris[i + 1]));
            }
            if (i < tris.Count)
            {
                AddU32(output, (GTri1 << 24) | Packed(tris[i]));
                AddU32(output, 0);
            }

            firstVertex += n;
        }

        AddU32(output, (uint)GEndDl << 24);
        AddU32(output, 0);
    }

    /// <summary>F3DEX2 stores buffer slots pre-multiplied by two.</summary>
    private static uint Packed(MeshTriangle t)
        => (uint)(((t.A * 2) << 16) | ((t.B * 2) << 8) | (t.C * 2));

    // ---- validation -------------------------------------------------------

    private static void Validate(MeshBuildModel model)
    {
        if (model.Parts.Count == 0)
            throw new InvalidDataException("A mesh needs at least one part.");
        if (model.Parts.Count > 256)
            throw new InvalidDataException($"{model.Parts.Count} parts; the format allows at most 256.");

        for (int p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p];
            if (part.SecondarySubMeshes.Count is not 0 && part.SecondarySubMeshes.Count != part.SubMeshes.Count)
                throw new InvalidDataException(
                    $"Part {p} has {part.SubMeshes.Count} sub-meshes but {part.SecondarySubMeshes.Count} " +
                    "in its second list. The format stores one count for both, so they must match.");

            foreach (var sub in part.SubMeshes.Concat(part.SecondarySubMeshes))
            {
                if (sub.Vertices.Count == 0)
                    throw new InvalidDataException($"Part {p} has a sub-mesh with no vertices.");

                if (sub.Vertices.Count > MaxVerticesPerSubMesh)
                    throw new InvalidDataException(
                        $"Part {p} has a sub-mesh with {sub.Vertices.Count:N0} vertices, past the " +
                        $"{MaxVerticesPerSubMesh:N0} ceiling. Split the mesh in your DCC tool.");

                foreach (var t in sub.Triangles)
                    if (t.A < 0 || t.B < 0 || t.C < 0 ||
                        t.A >= sub.Vertices.Count || t.B >= sub.Vertices.Count || t.C >= sub.Vertices.Count)
                        throw new InvalidDataException(
                            $"Part {p} has a triangle ({t.A},{t.B},{t.C}) outside its {sub.Vertices.Count}-vertex pool.");
            }
        }
    }

    // ---- little helpers ---------------------------------------------------

    private static void AddU32(List<byte> output, uint value)
    {
        output.Add((byte)(value >> 24)); output.Add((byte)(value >> 16));
        output.Add((byte)(value >> 8));  output.Add((byte)value);
    }

    private static void AddI16(List<byte> output, short value)
    {
        output.Add((byte)(value >> 8)); output.Add((byte)value);
    }
}
