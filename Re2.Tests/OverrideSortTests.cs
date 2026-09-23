using System.Collections.Generic;
using System.Linq;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>Ordering the Overrides table.</summary>
public class OverrideSortTests
{
    private static List<ProjectOverrides.Item> Rows() => new()
    {
        new ProjectOverrides.Item(30, new[] { 30 }, "texture", "assets/textures/texture30.tex", 900, 900),
        new ProjectOverrides.Item(10, new[] { 10 }, "model", "assets/models/mesh10.mesh", 500, 1234),
        new ProjectOverrides.Item(20, new[] { 20 }, "background", "assets/backgrounds/bg20.jpg", 700, 60),
    };

    [Fact]
    public void SortsByAssetIdByDefault()
    {
        var rows = Rows();
        OverridePanel.SortItems(rows, 1, ascending: true);

        Assert.Equal(new[] { 10, 20, 30 }, rows.Select(r => r.AssetId));

        OverridePanel.SortItems(rows, 1, ascending: false);
        Assert.Equal(new[] { 30, 20, 10 }, rows.Select(r => r.AssetId));
    }

    [Fact]
    public void SortsByCategoryAndByFile()
    {
        var rows = Rows();

        OverridePanel.SortItems(rows, 2, ascending: true);
        Assert.Equal(new[] { "background", "model", "texture" }, rows.Select(r => r.Category));

        OverridePanel.SortItems(rows, 5, ascending: true);
        Assert.Equal(new[] { 20, 10, 30 }, rows.Select(r => r.AssetId));   // backgrounds, models, textures
    }

    /// <summary>Size sorts by the byte count, not by the text in the column.</summary>
    [Fact]
    public void SortsBySizeNumericallyRatherThanByItsText()
    {
        var rows = Rows();
        OverridePanel.SortItems(rows, 3, ascending: true);

        Assert.Equal(new[] { 60, 900, 1234 }, rows.Select(r => r.ProjectSize));

        OverridePanel.SortItems(rows, 3, ascending: false);
        Assert.Equal(new[] { 1234, 900, 60 }, rows.Select(r => r.ProjectSize));
    }

    /// <summary>Equal keys keep asset order, so a rescan cannot shuffle rows that compare the same.</summary>
    [Fact]
    public void TiesAreBrokenByAssetId()
    {
        var rows = new List<ProjectOverrides.Item>
        {
            new(30, new[] { 30 }, "texture", "c.tex", 100, 100),
            new(10, new[] { 10 }, "texture", "a.tex", 100, 100),
            new(20, new[] { 20 }, "texture", "b.tex", 100, 100),
        };

        OverridePanel.SortItems(rows, 2, ascending: true);
        Assert.Equal(new[] { 10, 20, 30 }, rows.Select(r => r.AssetId));

        // Even reversed, the tie-break stays ascending -- otherwise equal rows would flip about.
        OverridePanel.SortItems(rows, 2, ascending: false);
        Assert.Equal(new[] { 10, 20, 30 }, rows.Select(r => r.AssetId));
    }
}
