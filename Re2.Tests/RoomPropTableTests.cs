using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>The per-room scenery lists.</summary>
public class RoomPropTableTests
{
    private readonly ITestOutputHelper _out;

    public RoomPropTableTests(ITestOutputHelper output) => _out = output;

    private static (List<RoomProp> Props, HashSet<int> Models) ReadProps()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        var models = new HashSet<int>();
        foreach (var entry in directory.Entries)
            if (directory.TryGetData(rom, entry, out var data) && MeshFile.TryParse(data, out _))
                models.Add(entry.Index);

        return (RoomPropTable.Read(ModelTextureTable.LoadMainOverlay(rom), models), models);
    }

    /// <summary>Everything the walk finds has to be a real model.</summary>
    [RomFact]
    public void EveryPropNamesARealModelAndARealTextureSet()
    {
        var (props, models) = ReadProps();
        var overlay = ModelTextureTable.LoadMainOverlay(TestRom.Rom);

        Assert.NotEmpty(props);

        foreach (var prop in props)
        {
            Assert.Contains(prop.MeshAssetId, models);
            Assert.NotNull(ModelTextureTable.ReadPair(overlay, prop.PairIndex));
        }
    }

    /// <summary>The "stopped too early" guard.</summary>
    [RomFact]
    public void TheRoomsAccountForNearlyEverySceneryModel()
    {
        var (props, models) = ReadProps();

        var scenery = models.Where(id => id is >= 7875 and <= 8083).ToHashSet();
        var named = props.Select(p => p.MeshAssetId).ToHashSet();
        var missing = scenery.Where(id => !named.Contains(id)).OrderBy(id => id).ToList();

        _out.WriteLine($"{scenery.Count} scenery models, {named.Count} named by rooms");
        _out.WriteLine("unnamed: " + string.Join(", ", missing));

        Assert.True(missing.Count <= 12,
                    $"{missing.Count} scenery models were not found, so the walk is stopping early");
    }

    /// <summary>Nothing outside the scenery range should turn up: that would mean a wrong turn.</summary>
    [RomFact]
    public void NoPropFallsOutsideTheSceneryRange()
    {
        var (props, _) = ReadProps();

        var strays = props.Where(p => p.MeshAssetId is < 7875 or > 8083)
                          .Select(p => p.MeshAssetId)
                          .Distinct()
                          .ToList();

        Assert.True(strays.Count == 0, "the walk strayed onto " + string.Join(", ", strays));
    }

    /// <summary>
    /// The texture set a room binds has to fit the model it binds it to, the same cross-check that
    /// confirms the character and item tables.
    /// </summary>
    [RomFact]
    public void TextureSetsMatchTheModelsTheyAreBoundTo()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var overlay = ModelTextureTable.LoadMainOverlay(rom);
        var (props, _) = ReadProps();

        int agree = 0, total = 0;

        foreach (var prop in props)
        {
            var entry = directory.Entries.First(e => e.Index == prop.MeshAssetId);
            directory.TryGetData(rom, entry, out var data);
            MeshFile.TryParse(data, out var mesh);

            var textures = ModelTextureTable.ReadPair(overlay, prop.PairIndex)!;

            total++;
            if (mesh!.TextureCount == textures.Count) agree++;
        }

        _out.WriteLine($"{agree}/{total} props agree with their mesh header");
        Assert.True(agree >= total * 0.9,
                    $"only {agree} of {total} agreed, so the pair indices are probably not pair indices");
    }

    /// <summary>The fixed list every room loads, which is where the walk starts.</summary>
    [RomFact]
    public void TheGlobalListIsFound()
    {
        var (props, _) = ReadProps();
        var global = props.Where(p => p.Stage == -1).ToList();

        Assert.Equal(9, global.Count);
        Assert.Contains(global, p => p.MeshAssetId == 7896 && p.PairIndex == 1826);
    }
}
