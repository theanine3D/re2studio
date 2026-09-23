using System.Collections.Generic;
using System.Linq;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>The parts of the shared multi-selection that do not touch ImGui.</summary>
public class SelectionTests
{
    [Fact]
    public void EmptySelectionHasNoPrimary()
    {
        var selection = new Selection();
        Assert.Equal(0, selection.Count);
        Assert.Equal(-1, selection.Primary);
        Assert.False(selection.IsMultiple);
    }

    [Fact]
    public void PrimaryIsTheFirstInListOrder()
    {
        var selection = new Selection();
        selection.SetRange(5, 4, limit: 100);

        Assert.Equal(4, selection.Count);
        Assert.Equal(5, selection.Primary);
        Assert.True(selection.IsMultiple);
        Assert.Equal(new[] { 5, 6, 7, 8 }, selection.Indices);
    }

    [Fact]
    public void SetRangeStopsAtTheEndOfTheList()
    {
        var selection = new Selection();
        selection.SetRange(8, 5, limit: 10);

        Assert.Equal(new[] { 8, 9 }, selection.Indices);
    }

    [Fact]
    public void SetReplacesTheWholeSelection()
    {
        var selection = new Selection();
        selection.SetRange(0, 6, limit: 10);
        selection.Set(3);

        Assert.Equal(1, selection.Count);
        Assert.Equal(3, selection.Primary);
    }

    [Fact]
    public void ConstrainDropsIndicesPastTheEnd()
    {
        // What happens when a different ROM is loaded and a list gets shorter underneath a selection.
        var selection = new Selection();
        selection.SetRange(2, 8, limit: 100);
        selection.Constrain(5);

        Assert.Equal(new[] { 2, 3, 4 }, selection.Indices);
        Assert.Equal(2, selection.Primary);
    }

    [Fact]
    public void ToAssetIdsMapsThroughThePanelsOwnNumbering()
    {
        // Panels index by position; labels are keyed by asset id, and the two are not the same.
        var ids = new List<int> { 900, 901, 902, 903, 904 };

        var selection = new Selection();
        selection.SetRange(1, 3, ids.Count);

        Assert.Equal(new[] { 901, 902, 903 }, selection.ToAssetIds(i => ids[i]));
    }

    [Fact]
    public void ToAssetIdsCollapsesDuplicates()
    {
        // Backgrounds share blobs, so two positions can resolve to one asset -- labelling it twice
        // would be harmless but writing the same key twice is not something the caller should see.
        var ids = new List<int> { 7, 7, 8 };

        var selection = new Selection();
        selection.SetRange(0, 3, ids.Count);

        Assert.Equal(new[] { 7, 8 }, selection.ToAssetIds(i => ids[i]));
    }

    [Fact]
    public void ClearLeavesNothingSelected()
    {
        var selection = new Selection();
        selection.SetRange(0, 4, limit: 10);
        selection.Clear();

        Assert.Equal(0, selection.Count);
        Assert.Equal(-1, selection.Primary);
        Assert.Empty(selection.Indices);
    }
}
