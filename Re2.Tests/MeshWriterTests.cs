using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

public sealed class MeshWriterTests
{
    private readonly ITestOutputHelper _output;
    public MeshWriterTests(ITestOutputHelper output) => _output = output;

    private static List<MeshFile> LoadMeshes()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var meshes = new List<MeshFile>();

        foreach (var entry in directory.Entries)
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (MeshFile.TryParse(data, out var mesh)) meshes.Add(mesh);
        }

        return meshes;
    }

    /// <summary>
    /// The decisive test for the encoder: re-encode every mesh in the ROM and parse the result back
    /// with the same reader the game's data goes through.
    /// </summary>
    [RomFact]
    public void EveryRomMeshSurvivesAWriteAndReparse()
    {
        var meshes = LoadMeshes();
        Assert.True(meshes.Count > 300, $"only {meshes.Count} meshes were found");

        long vertices = 0, triangles = 0;

        foreach (var original in meshes)
        {
            var rebuilt = MeshFile.Parse(MeshWriter.Write(MeshBuildModel.FromMeshFile(original)));

            Assert.Equal(original.PartCount, rebuilt.PartCount);
            Assert.Equal(original.TextureCount, rebuilt.TextureCount);
            Assert.Equal(original.TotalTriangles, rebuilt.TotalTriangles);

            for (int p = 0; p < original.PartCount; p++)
            {
                var before = original.Parts[p].SubMeshes;
                var after = rebuilt.Parts[p].SubMeshes;
                Assert.Equal(before.Count, after.Count);

                for (int s = 0; s < before.Count; s++)
                {
                    // Compare the geometry each triangle actually describes, not the raw vertex
                    // arrays: chunking a sub-mesh across several G_VTX loads duplicates a few
                    // vertices at the chunk boundaries, which is correct and expected.
                    Assert.Equal(Resolved(before[s]), Resolved(after[s]));
                    Assert.Equal(before[s].TextureIndex, after[s].TextureIndex);
                    Assert.Equal(before[s].Colour0, after[s].Colour0);
                    Assert.Equal(before[s].Colour1, after[s].Colour1);
                    Assert.Equal(before[s].IsSecondary, after[s].IsSecondary);
                }
            }

            vertices += original.TotalVertices;
            triangles += original.TotalTriangles;
        }

        _output.WriteLine($"{meshes.Count} meshes, {vertices:N0} vertices, {triangles:N0} triangles round-tripped");
    }


    /// <summary>The three positions each triangle names, which is the geometry that must survive.</summary>
    private static List<(MeshVertex A, MeshVertex B, MeshVertex C)> Resolved(SubMesh sub)
        => sub.Triangles
            .Select(t => (sub.Vertices[t.A], sub.Vertices[t.B], sub.Vertices[t.C]))
            .ToList();

    /// <summary>Nothing may be silently dropped when a display list is walked.</summary>
    [RomFact]
    public void EveryTriangleCommandInTheRomResolves()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        long commands = 0, parsed = 0;
        int meshes = 0;

        foreach (var entry in directory.Entries)
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var mesh)) continue;
            meshes++;

            foreach (var part in mesh.Parts)
            foreach (var sub in part.SubMeshes)
            {
                parsed += sub.Triangles.Count;
                commands += CountTriangleCommands(data, sub.DisplayListOffset);
            }
        }

        _output.WriteLine($"{meshes} meshes: {commands:N0} triangle commands, {parsed:N0} triangles parsed");
        Assert.True(commands > 50_000, $"only {commands} triangle commands were seen");
        Assert.Equal(commands, parsed);
    }

    private static int CountTriangleCommands(byte[] data, int at)
    {
        int count = 0;
        for (int i = 0; i < 4096 && at >= 0 && at + 8 <= data.Length; i++, at += 8)
        {
            byte op = data[at];
            if (op == 0xDF) break;
            if (op == 0x05) count++;
            else if (op == 0x06) count += 2;
        }
        return count;
    }

    /// <summary>36 retail models put geometry in a part's second list.</summary>
    [RomFact]
    public void SecondaryGeometryListsSurvive()
    {
        var withSecondary = LoadMeshes()
            .Where(m => m.Parts.Any(p => p.SubMeshes.Any(s => s.IsSecondary)))
            .ToList();

        Assert.True(withSecondary.Count >= 30, $"only {withSecondary.Count} meshes use a second list");

        foreach (var original in withSecondary)
        {
            var rebuilt = MeshFile.Parse(MeshWriter.Write(MeshBuildModel.FromMeshFile(original)));
            Assert.Equal(original.Parts.Sum(p => p.SubMeshes.Count(s => s.IsSecondary)),
                         rebuilt.Parts.Sum(p => p.SubMeshes.Count(s => s.IsSecondary)));
        }
    }

    /// <summary>
    /// A written mesh must look like a mesh to the same probe that scans the ROM, or the tools --
    /// and the game's own loader -- would not recognise it.
    /// </summary>
    [RomFact]
    public void WrittenMeshesPassTheFormatProbe()
    {
        foreach (var mesh in LoadMeshes().Take(50))
        {
            var bytes = MeshWriter.Write(MeshBuildModel.FromMeshFile(mesh));

            Assert.True(MeshFile.LooksLikeMesh(bytes));

            // The part table must tile exactly to the end of the file, which is the check that
            // catches a wrong record stride.
            var rebuilt = MeshFile.Parse(bytes);
            Assert.Equal(bytes.Length, rebuilt.PartTableOffset + rebuilt.PartCount * MeshFile.PartRecordSize);
        }
    }

    /// <summary>Empty parts have to keep their slot, or every later part shifts and animation breaks.</summary>
    [Fact]
    public void EmptyPartsKeepTheirSlot()
    {
        var model = new MeshBuildModel { TextureCount = 1 };
        model.Parts.Add(new MeshBuildPart());                       // empty
        model.Parts.Add(new MeshBuildPart());
        model.Parts.Add(new MeshBuildPart());                       // empty

        model.Parts[1].SubMeshes.Add(Triangle(textureIndex: 0));

        var rebuilt = MeshFile.Parse(MeshWriter.Write(model));

        Assert.Equal(3, rebuilt.PartCount);
        Assert.Empty(rebuilt.Parts[0].SubMeshes);
        Assert.Single(rebuilt.Parts[1].SubMeshes);
        Assert.Empty(rebuilt.Parts[2].SubMeshes);
        Assert.Equal(0, rebuilt.Parts[1].SubMeshes[0].TextureIndex);
    }

    [Fact]
    public void UntexturedGeometryKeepsItsSentinel()
    {
        var model = new MeshBuildModel();
        model.Parts.Add(new MeshBuildPart());
        model.Parts[0].SubMeshes.Add(Triangle(textureIndex: -1));

        var rebuilt = MeshFile.Parse(MeshWriter.Write(model));
        Assert.Equal(-1, rebuilt.Parts[0].SubMeshes[0].TextureIndex);
        Assert.False(rebuilt.Parts[0].SubMeshes[0].HasTexture);
    }

    /// <summary>An odd triangle count needs a G_TRI1 after the G_TRI2 pairs.</summary>
    [Fact]
    public void OddTriangleCountsRoundTrip()
    {
        foreach (int count in new[] { 1, 2, 3, 4, 5, 9 })
        {
            var sub = new MeshBuildSubMesh();
            for (int i = 0; i < count + 2; i++)
                sub.Vertices.Add(new MeshVertex((short)i, 0, 0, 0, 0, 0, 127, 0, 255));
            for (int i = 0; i < count; i++)
                sub.Triangles.Add(new MeshTriangle(0, i + 1, i + 2 < sub.Vertices.Count ? i + 2 : 1));

            var model = new MeshBuildModel();
            model.Parts.Add(new MeshBuildPart());
            model.Parts[0].SubMeshes.Add(sub);

            var rebuilt = MeshFile.Parse(MeshWriter.Write(model));
            Assert.Equal(count, rebuilt.Parts[0].SubMeshes[0].Triangles.Count);
        }
    }

    /// <summary>
    /// A sub-mesh larger than the 64-entry vertex buffer must be chunked into several G_VTX loads,
    /// not rejected and not truncated. This is what lets an ordinary DCC mesh import at all.
    /// </summary>
    [Fact]
    public void LargeSubMeshesAreChunkedAcrossSeveralVertexLoads()
    {
        // A strip of 300 triangles over 302 vertices: far past one load.
        var sub = new MeshBuildSubMesh { TextureIndex = 0 };
        for (int i = 0; i < 302; i++)
            sub.Vertices.Add(new MeshVertex((short)(i * 3), (short)(i % 7), 0, 0, 0, 0, 127, 0, 255));
        for (int i = 0; i + 2 < 302; i++)
            sub.Triangles.Add(new MeshTriangle(i, i + 1, i + 2));

        var model = new MeshBuildModel();
        model.Parts.Add(new MeshBuildPart());
        model.Parts[0].SubMeshes.Add(sub);

        var rebuilt = MeshFile.Parse(MeshWriter.Write(model));
        var result = rebuilt.Parts[0].SubMeshes[0];

        Assert.Single(rebuilt.Parts[0].SubMeshes);
        Assert.Equal(sub.Triangles.Count, result.Triangles.Count);
        Assert.True(result.Vertices.Count >= 302, "vertices were lost in chunking");

        // Every triangle must still describe the same three positions it started with.
        for (int i = 0; i < sub.Triangles.Count; i++)
        {
            var before = sub.Triangles[i];
            var after = result.Triangles[i];
            Assert.Equal(sub.Vertices[before.A].X, result.Vertices[after.A].X);
            Assert.Equal(sub.Vertices[before.B].X, result.Vertices[after.B].X);
            Assert.Equal(sub.Vertices[before.C].X, result.Vertices[after.C].X);
        }
    }

    [Fact]
    public void MismatchedSecondaryListIsRejected()
    {
        var model = new MeshBuildModel();
        var part = new MeshBuildPart();
        part.SubMeshes.Add(Triangle(0));
        part.SubMeshes.Add(Triangle(0));
        part.SecondarySubMeshes.Add(Triangle(0));       // one, not two
        model.Parts.Add(part);

        var ex = Assert.Throws<System.IO.InvalidDataException>(() => MeshWriter.Write(model));
        Assert.Contains("one count for both", ex.Message);
    }

    private static MeshBuildSubMesh Triangle(int textureIndex)
    {
        var sub = new MeshBuildSubMesh { TextureIndex = textureIndex };
        sub.Vertices.Add(new MeshVertex(0, 0, 0, 0, 0, 0, 127, 0, 255));
        sub.Vertices.Add(new MeshVertex(100, 0, 0, 16383, 0, 0, 127, 0, 255));
        sub.Vertices.Add(new MeshVertex(0, 100, 0, 0, 16383, 0, 127, 0, 255));
        sub.Triangles.Add(new MeshTriangle(0, 1, 2));
        return sub;
    }
}
