using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>The item model table: weapons, ammunition, herbs and the rest.</summary>
public class ItemTableTests
{
    private readonly ITestOutputHelper _out;

    public ItemTableTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheTableEndsExactlyWhereThePairTableBegins()
    {
        uint end = ItemTable.TableAddress + (uint)(ItemTable.Count * ItemTable.RecordSize);
        Assert.Equal(ModelTextureTable.PairTableAddress, end);
        Assert.Equal(66, ItemTable.Count);
    }

    [RomFact]
    public void EverySlotResolvesToAMeshAndATextureSet()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var items = ItemTable.Read(rom);

        Assert.Equal(ItemTable.Count, items.Count);

        foreach (var item in items)
        {
            var entry = directory.Entries.FirstOrDefault(e => e.Index == item.MeshAssetId);
            Assert.True(entry is not null, $"slot {item.Index} names mesh {item.MeshAssetId}, which is not an asset");

            Assert.True(directory.TryGetData(rom, entry!, out var data), $"mesh {item.MeshAssetId} would not decode");
            Assert.True(MeshFile.TryParse(data, out _), $"asset {item.MeshAssetId} is not a model");

            Assert.NotEmpty(item.Textures.TextureIds);
            foreach (int textureId in item.Textures.TextureIds)
                Assert.Contains(directory.Entries, e => e.Index == textureId);
        }
    }

    /// <summary>
    /// The cross-check that the two tables are genuinely aligned: a pair index read from this table has
    /// to describe the mesh it sits beside.
    /// </summary>
    [RomFact]
    public void PairRecordsDescribeTheMeshesTheySitBeside()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var items = ItemTable.Read(rom);

        int agree = 0, off = 0;

        foreach (var item in items)
        {
            var entry = directory.Entries.First(e => e.Index == item.MeshAssetId);
            directory.TryGetData(rom, entry, out var data);
            MeshFile.TryParse(data, out var mesh);

            int difference = mesh!.TextureCount - item.Textures.Count;
            if (difference == 0) agree++;
            else
            {
                off++;
                _out.WriteLine($"slot {item.Index} mesh {item.MeshAssetId}: " +
                               $"header {mesh.TextureCount}, pair {item.Textures.Count}");
            }

            Assert.True(System.Math.Abs(difference) <= 1,
                        $"slot {item.Index} mesh {item.MeshAssetId} is out by {difference} textures");
        }

        _out.WriteLine($"{agree}/{items.Count} agree exactly");
        Assert.True(agree >= 60, $"only {agree} of {items.Count} agreed, so the tables are probably misaligned");
    }

    /// <summary>Slot 0 is Leon.</summary>
    [RomFact]
    public void SlotZeroIsLeonWithElevenTextures()
    {
        var items = ItemTable.Read(TestRom.Rom);
        var first = items[0];

        Assert.Equal(5776, first.MeshAssetId);
        Assert.Equal(0, first.PairIndex);
        Assert.Equal(11, first.Textures.Count);
    }

    /// <summary>
    /// The player-model block at the head of the table is really a block, and really ends where <see
    /// cref="ItemTable.FirstItemSlot"/> says.
    /// </summary>
    [RomFact]
    public void ThePlayerModelBlockEndsWhereTheBoundarySaysItDoes()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var items = ItemTable.Read(rom);

        int PartsOf(int meshAssetId)
        {
            var entry = directory.Entries.First(e => e.Index == meshAssetId);
            directory.TryGetData(rom, entry, out var data);
            MeshFile.TryParse(data, out var mesh);
            return mesh!.Parts.Count;
        }

        // A body model has many parts; an item has few. The gap between the two is empty.
        int smallestPlayer = items.Where(i => i.Index < ItemTable.FirstItemSlot).Min(i => PartsOf(i.MeshAssetId));
        int largestItem = items.Where(i => i.Index >= ItemTable.FirstItemSlot).Max(i => PartsOf(i.MeshAssetId));

        _out.WriteLine($"fewest parts before the boundary: {smallestPlayer}; most after it: {largestItem}");
        Assert.True(largestItem < smallestPlayer,
                    $"an item has {largestItem} parts and a player model only {smallestPlayer}, " +
                    "so the boundary is in the wrong place");

        // And nothing at or after the boundary is named by the character asset table, while the
        // block before it draws several of its meshes from there.
        var overlay = ModelTextureTable.LoadMainOverlay(rom);
        var characterMeshes = new HashSet<int>();

        int records = (int)((ItemTable.TableAddress - ModelTextureTable.CharacterAssetTableAddress) / 16);
        for (int i = 0; i < records; i++)
        {
            int mesh = overlay.U16(ModelTextureTable.CharacterAssetTableAddress + (uint)i * 16 + 14);
            if (mesh != 0xFFFF) characterMeshes.Add(mesh);
        }

        Assert.DoesNotContain(items.Where(i => i.Index >= ItemTable.FirstItemSlot),
                              i => characterMeshes.Contains(i.MeshAssetId));

        Assert.Contains(items.Where(i => i.Index < ItemTable.FirstItemSlot),
                        i => characterMeshes.Contains(i.MeshAssetId));
    }

    /// <summary>The small single-part meshes further down the table are the items themselves.</summary>
    [RomFact]
    public void TheTableReachesTheSmallItemMeshes()
    {
        var items = ItemTable.Read(TestRom.Rom);
        var meshes = items.Select(i => i.MeshAssetId).ToHashSet();

        // 5843 is the first of the stride-three run of item models.
        Assert.Contains(5843, meshes);
        Assert.Contains(5846, meshes);
        Assert.Contains(5921, meshes);

        Assert.True(meshes.Count >= 45, $"only {meshes.Count} distinct meshes reached");
    }
}
