using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public class MeshFileTests
{
    private static List<(AssetEntry Entry, MeshFile Mesh)> LoadMeshes()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);
        var result = new List<(AssetEntry, MeshFile)>();

        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (MeshFile.TryParse(data, out var mesh)) result.Add((entry, mesh));
        }
        return result;
    }

    [RomFact]
    public void FindsTheMeshSet()
    {
        var meshes = LoadMeshes();
        Assert.Equal(355, meshes.Count);
        Assert.True(meshes.Sum(m => m.Mesh.TotalVertices) > 80000);
        Assert.True(meshes.Sum(m => m.Mesh.TotalTriangles) > 75000);
    }

    /// <summary>
    /// The mesh referenced by character 0's entry [7] in the table at RAM 0x80126C80.
    /// </summary>
    [RomFact]
    public void ParsesTheFirstCharacterMesh()
    {
        var mesh = LoadMeshes().Single(m => m.Entry.Index == 5599).Mesh;

        // 17 parts: torso, pelvis, head, two legs (thigh/shin/foot) and two arms (upper/fore/hand).
        Assert.Equal(17, mesh.PartCount);
        Assert.Equal(1377, mesh.TotalVertices);
        Assert.Equal(850, mesh.TotalTriangles);
        Assert.Equal(13, mesh.TextureCount);
        Assert.Equal(0xB2B2B2FFu, mesh.Parts[0].SubMeshes[0].Colour0);
    }

    /// <summary>
    /// The strongest structural check: a sub-mesh's vertex block must be exactly vertexCount * 16
    /// bytes and end where its display list begins.
    /// </summary>
    [RomFact]
    public void VertexBlocksAreContiguousWithTheirDisplayLists()
    {
        foreach (var (entry, mesh) in LoadMeshes())
            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                {
                    if (sub.VertexCount == 0) continue;
                    Assert.True(sub.DisplayListOffset == sub.VertexOffset + sub.VertexCount * MeshFile.VertexSize,
                        $"asset {entry.Index} part {part.Index} sub {sub.Index}: " +
                        $"{sub.VertexCount} verts at 0x{sub.VertexOffset:X} should end at the display list 0x{sub.DisplayListOffset:X}");
                }
    }

    /// <summary>The part table must tile exactly to the end of the file.</summary>
    [RomFact]
    public void PartTableRunsToTheEndOfTheFile()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);

        foreach (var (entry, mesh) in LoadMeshes())
        {
            dir.TryGetData(rom, entry, out var data);
            Assert.Equal(data.Length, mesh.PartTableOffset + mesh.PartCount * MeshFile.PartRecordSize);
        }
    }

    [RomFact]
    public void EveryTriangleIndexIsInRange()
    {
        foreach (var (entry, mesh) in LoadMeshes())
            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                    foreach (var t in sub.Triangles)
                    {
                        Assert.InRange(t.A, 0, sub.Vertices.Count - 1);
                        Assert.InRange(t.B, 0, sub.Vertices.Count - 1);
                        Assert.InRange(t.C, 0, sub.Vertices.Count - 1);
                    }
    }

    [RomFact]
    public void ReadsExactlyTheDeclaredVertexCount()
    {
        foreach (var (_, mesh) in LoadMeshes())
            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                    Assert.Equal(sub.VertexCount, sub.Vertices.Count);
    }

    /// <summary>Degenerate triangles would mean the index stride is wrong.</summary>
    [RomFact]
    public void TrianglesAreNotDegenerate()
    {
        int degenerate = 0, total = 0;
        foreach (var (_, mesh) in LoadMeshes())
            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                    foreach (var t in sub.Triangles)
                    {
                        total++;
                        if (t.A == t.B || t.B == t.C || t.A == t.C) degenerate++;
                    }

        Assert.True(degenerate < total / 100, $"{degenerate} of {total} triangles are degenerate");
    }

    /// <summary>Each sub-mesh should occupy real volume.</summary>
    [RomFact]
    public void SubMeshBoundsAreNotDegenerate()
    {
        int collapsed = 0, total = 0;

        foreach (var (_, mesh) in LoadMeshes())
            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                {
                    if (sub.Vertices.Count < 4) continue;
                    total++;

                    int minX = int.MaxValue, maxX = int.MinValue;
                    int minY = int.MaxValue, maxY = int.MinValue;
                    int minZ = int.MaxValue, maxZ = int.MinValue;

                    foreach (var v in sub.Vertices)
                    {
                        minX = System.Math.Min(minX, v.X); maxX = System.Math.Max(maxX, v.X);
                        minY = System.Math.Min(minY, v.Y); maxY = System.Math.Max(maxY, v.Y);
                        minZ = System.Math.Min(minZ, v.Z); maxZ = System.Math.Max(maxZ, v.Z);
                    }

                    int axes = (maxX > minX ? 1 : 0) + (maxY > minY ? 1 : 0) + (maxZ > minZ ? 1 : 0);
                    if (axes < 2) collapsed++;
                }

        Assert.True(total > 500, $"only {total} sub-meshes to check");
        Assert.True(collapsed < total / 50, $"{collapsed} of {total} sub-meshes have collapsed bounds");
    }

    [Fact]
    public void RejectsNonMeshData() => Assert.False(MeshFile.TryParse(new byte[64], out _));
}

public class MeshUvTests
{
    /// <summary>
    /// Texture coordinates are normalised to a fixed 0..512 range, not scaled by the texture size.
    /// </summary>
    [RomFact]
    public void TextureCoordinatesFillTheFixedRangeAndNeverExceedIt()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);

        float min = float.MaxValue, max = float.MinValue;
        long counted = 0;

        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var mesh)) continue;

            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                    foreach (var v in sub.Vertices)
                    {
                        min = System.Math.Min(min, System.Math.Min(v.U, v.V));
                        max = System.Math.Max(max, System.Math.Max(v.U, v.V));
                        counted++;
                    }
        }

        Assert.True(counted > 100_000, $"only {counted} coordinates checked");
        Assert.InRange(min, 0f, 1f);
        Assert.InRange(max, 0.9f, 1.0f);   // the range is genuinely used, right up to the top
    }
}
