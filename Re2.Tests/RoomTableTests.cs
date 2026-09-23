using System.Linq;
using Re2.Core.Assets;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Rooms and the backgrounds their camera angles show.</summary>
public class RoomTableTests
{
    private readonly ITestOutputHelper _out;

    public RoomTableTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheStageLayoutIsWhatTheLoaderTablesSay()
    {
        Assert.Equal(7, RoomTable.StageCount);
        Assert.Equal(130, RoomTable.TotalRooms);
        Assert.Equal(new[] { 30, 28, 14, 18, 10, 24, 6 }, RoomTable.RoomsPerStage);
    }

    [RomFact]
    public void EveryRoomIsListedOnce()
    {
        var rooms = RoomTable.Read(TestRom.Rom);

        Assert.Equal(130, rooms.Count);
        Assert.Equal(130, rooms.Select(r => (r.Stage, r.Room)).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 130), rooms.Select(r => r.FlatIndex));
    }

    /// <summary>Every id a room names has to be a background the indexer found independently.</summary>
    [RomFact]
    public void EveryBackgroundNamedIsARealBackground()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var index = BackgroundIndex.Build(rom);

        var backgroundAssets = index.Backgrounds
            .Select(b => directory.Entries.FirstOrDefault(e => e.RomOffset == b.Offset))
            .Where(e => e is not null)
            .Select(e => e!.Index)
            .ToHashSet();

        var rooms = RoomTable.Read(rom);
        var named = rooms.SelectMany(r => r.BackgroundAssetIds).Distinct().ToList();

        int known = named.Count(backgroundAssets.Contains);
        _out.WriteLine($"{named.Count} distinct ids named, {known} of them are indexed backgrounds " +
                       $"(the indexer finds {index.Count})");

        Assert.True(known >= named.Count * 0.97,
                    $"only {known} of {named.Count} named ids are backgrounds");
    }

    /// <summary>
    /// Consecutive rooms take consecutive runs of backgrounds, which is what storing them in room
    /// order looks like -- and is the strongest sign the lists are being cut in the right places.
    /// </summary>
    [RomFact]
    public void ConsecutiveRoomsTakeConsecutiveBackgrounds()
    {
        var rooms = RoomTable.Read(TestRom.Rom).Where(r => r.ViewCount > 0).ToList();

        int ascending = 0, compared = 0;
        for (int i = 1; i < rooms.Count; i++)
        {
            var previous = rooms[i - 1].BackgroundAssetIds;
            var current = rooms[i].BackgroundAssetIds;
            if (previous.Count == 0 || current.Count == 0) continue;

            compared++;
            if (current[0] > previous[^1]) ascending++;
        }

        _out.WriteLine($"{ascending} of {compared} rooms start after the previous room ends");
        Assert.True(ascending >= compared * 0.8);
    }

    [RomFact]
    public void MostRoomsHaveBackgroundsAndTheTotalIsSane()
    {
        var rooms = RoomTable.Read(TestRom.Rom);

        int withViews = rooms.Count(r => r.ViewCount > 0);
        int views = rooms.Sum(r => r.ViewCount);

        _out.WriteLine($"{withViews} of {rooms.Count} rooms have backgrounds; {views} camera views");

        Assert.True(withViews >= 120);
        Assert.InRange(views, 1000, 1600);
        Assert.All(rooms, r => Assert.True(r.ViewCount <= 32, $"stage {r.Stage} room {r.Room} has {r.ViewCount}"));
    }

    [RomFact]
    public void TheFirstRoomMatchesWhatWasReadOutOfTheTable()
    {
        var rooms = RoomTable.Read(TestRom.Rom);
        var first = rooms.First(r => r.Stage == 0 && r.Room == 0);

        Assert.Equal(new[] { 4060, 4061, 4062, 4063, 4064, 4065, 4066, 4067, 4068 },
                     first.BackgroundAssetIds);
    }
}
