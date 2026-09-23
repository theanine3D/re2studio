using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public class ModelTextureTableTests
{
    private static ModelTextureTable.Overlay Overlay() => ModelTextureTable.LoadMainOverlay(TestRom.Rom);

    [RomFact]
    public void ResolvesTheFirstCharacter()
    {
        var character = ModelTextureTable.ReadCharacters(Overlay())[0];

        Assert.Equal(0, character.Index);
        Assert.Equal(5599, character.MeshAssetId);
        Assert.Equal(276, character.PairIndex);
        Assert.Equal(13, character.Textures.Count);
        Assert.Equal(Enumerable.Range(6040, 13), character.Textures.TextureIds);
        Assert.Equal(new[] { 5593, 5594, 5595, 5596, 5597, 5598 }, character.AnimationAssetIds);
    }

    /// <summary>
    /// The cross-check that ties the two tables together: a mesh states its own texture count in its
    /// header, and the pair table independently states how many textures the character has.
    /// </summary>
    [RomFact]
    public void MeshTextureCountAgreesWithThePairTable()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);
        var overlay = Overlay();

        var counts = new Dictionary<int, int>();
        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (MeshFile.TryParse(data, out var mesh)) counts[entry.Index] = mesh.TextureCount;
        }

        int matched = 0, total = 0;
        for (int table = 0; table < ModelTextureTable.EntityTableAddresses.Length; table++)
            foreach (var character in ModelTextureTable.ReadCharacters(overlay, table))
            {
                total++;
                if (counts.TryGetValue(character.MeshAssetId, out int declared) && declared == character.Textures.Count)
                    matched++;
            }

        Assert.True(total > 200, $"only {total} characters found");
        Assert.True(matched >= total * 95 / 100, $"only {matched} of {total} characters agree");
    }

    [RomFact]
    public void EveryReferencedTextureIdResolvesToATexture()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);
        var byId = dir.Entries.ToDictionary(e => e.Index);

        int checkedIds = 0;
        foreach (var character in ModelTextureTable.ReadCharacters(Overlay()))
            foreach (int id in character.Textures.TextureIds)
            {
                Assert.True(byId.TryGetValue(id, out var entry), $"texture id {id} is not in the directory");
                Assert.True(dir.TryGetData(rom, entry!, out var data));
                Assert.True(TextureFile.LooksLikeTexture(data), $"asset {id} is not a texture");
                checkedIds++;
            }

        Assert.True(checkedIds > 100, $"only {checkedIds} texture ids checked");
    }
}

public class MeshMaterialTests
{
    /// <summary>A material block lies inside the file and before the vertices it describes.</summary>
    [RomFact]
    public void MaterialBlocksPrecedeTheirVertices()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);

        int adjacent = 0, total = 0;
        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var mesh)) continue;

            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                {
                    total++;
                    Assert.InRange(sub.MaterialOffset, 0, data.Length - 16);
                    Assert.True(sub.MaterialOffset < sub.VertexOffset,
                        $"material 0x{sub.MaterialOffset:X} is not before vertices 0x{sub.VertexOffset:X}");
                    if (sub.MaterialOffset == sub.VertexOffset - 16) adjacent++;
                }
        }

        Assert.True(total > 2000, $"only {total} sub-meshes checked");
        Assert.True(adjacent > total * 90 / 100, $"only {adjacent} of {total} materials are adjacent");
    }

    /// <summary>Every textured sub-mesh must select a texture the model actually has.</summary>
    [RomFact]
    public void TextureIndicesStayWithinTheModelsTextureCount()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);

        int textured = 0, untextured = 0;
        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var mesh)) continue;
            if (mesh.TextureCount <= 0) continue;

            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                {
                    if (!sub.HasTexture) { untextured++; Assert.Equal(-1, sub.TextureIndex); continue; }
                    textured++;
                    Assert.InRange(sub.TextureIndex, 0, mesh.TextureCount - 1);
                }
        }

        Assert.True(textured > 1000, $"only {textured} textured sub-meshes checked");
        Assert.True(untextured < textured / 10, $"{untextured} untextured sub-meshes looks too high");
    }
}
