using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Studio;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Ordering the sample list by something other than id.</summary>
public class SoundSortTests
{
    private readonly ITestOutputHelper _out;

    public SoundSortTests(ITestOutputHelper output) => _out = output;

    private static (SoundDirectory Sound, List<int> Rows) Load()
    {
        var rom = TestRom.Rom;
        var sound = SoundDirectory.Read(rom, AssetDirectory.Read(rom));
        return (sound, Enumerable.Range(0, sound.Samples.Count).ToList());
    }

    /// <summary>The column indices the panel passes in, pinned against the header row's order.</summary>
    private const int Id = 0, Offset = 2, Stored = 3, Rate = 4, Length = 5;

    [RomFact]
    public void SortingByStoredSizePutsTheLargestFirst()
    {
        var (sound, rows) = Load();

        SoundPanel.SortVisible(sound, rows, Stored, ascending: false);

        var sizes = rows.Select(i => sound.Samples[i].StoredSize).ToList();
        Assert.Equal(sizes.OrderByDescending(x => x), sizes);

        _out.WriteLine($"largest: sample {sound.Samples[rows[0]].Index}, " +
                       $"{sound.Samples[rows[0]].StoredSize:N0} bytes, " +
                       $"{sound.Samples[rows[0]].Seconds:0.0}s");

        // The biggest sample really is far bigger than the median, which is what makes this useful.
        int median = sizes[sizes.Count / 2];
        Assert.True(sizes[0] > median * 4, $"largest {sizes[0]} is not much above the median {median}");
    }

    [RomFact]
    public void SortingAscendingIsTheReverseOrder()
    {
        var (sound, rows) = Load();

        SoundPanel.SortVisible(sound, rows, Stored, ascending: true);
        var sizes = rows.Select(i => sound.Samples[i].StoredSize).ToList();

        Assert.Equal(sizes.OrderBy(x => x), sizes);
    }

    [RomFact]
    public void EqualValuesFallBackToIdSoTheOrderIsStable()
    {
        var (sound, rows) = Load();

        SoundPanel.SortVisible(sound, rows, Stored, ascending: false);

        for (int i = 1; i < rows.Count; i++)
        {
            var previous = sound.Samples[rows[i - 1]];
            var current = sound.Samples[rows[i]];
            if (previous.StoredSize == current.StoredSize)
                Assert.True(previous.Index < current.Index,
                            $"samples {previous.Index} and {current.Index} share a size but are out of id order");
        }
    }

    [RomFact]
    public void SortingKeepsEveryRowExactlyOnce()
    {
        var (sound, rows) = Load();
        var before = rows.ToHashSet();

        foreach (int column in new[] { Id, Offset, Stored, Rate, Length })
        {
            SoundPanel.SortVisible(sound, rows, column, ascending: false);
            Assert.Equal(before.Count, rows.Count);
            Assert.Equal(before, rows.ToHashSet());
        }
    }

    [RomFact]
    public void TheDefaultOrderIsById()
    {
        var (sound, rows) = Load();
        rows.Reverse();
        var reversed = rows.ToList();

        SoundPanel.SortVisible(sound, rows, Id, ascending: true);

        // Column 0 ascending is the list's natural order, so the rows are left exactly as built --
        // the filter already produced them in id order and re-sorting would be wasted work.
        Assert.Equal(reversed, rows);

        SoundPanel.SortVisible(sound, rows, Id, ascending: false);
        var ids = rows.Select(i => sound.Samples[i].Index).ToList();
        Assert.Equal(ids.OrderByDescending(x => x), ids);
    }
}
