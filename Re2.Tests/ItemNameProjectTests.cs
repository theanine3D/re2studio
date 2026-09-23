using System;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Project;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

public sealed class ItemNameProjectTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "re2-names-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp */ }
    }

    private RomFile Fresh() => RomFile.FromBytes((byte[])TestRom.Rom.Data.Clone());

    private static bool ApplyOk(string folder, RomFile output, out bool changed, out string error)
    {
        var result = OverlayText.Apply(folder, output);
        changed = result.Applied;
        error = result.Error;
        return error.Length == 0;
    }

    [RomFact]
    public void ExtractWritesTheNamesAndTheyReadBack()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        Assert.True(ItemNameFile.ExistsIn(_folder));
        Assert.True(ItemNameFile.TryRead(_folder, out var names, out string error), error);

        Assert.Equal(ItemNames.Count, names.Count);
        Assert.Equal(ItemNames.Read(TestRom.Rom), names);
    }

    [RomFact]
    public void ApplyingUnchangedNamesLeavesTheRomAlone()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        var output = Fresh();
        var before = (byte[])output.Data.Clone();

        Assert.True(ApplyOk(_folder, output, out bool changed, out string error), error);
        Assert.False(changed);
        Assert.True(before.SequenceEqual(output.Data));
    }

    [RomFact]
    public void ARenameSurvivesABuild()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        Assert.True(ItemNameFile.TryRead(_folder, out var names, out string error), error);
        names[38] = "Emerald Leaf";
        ItemNameFile.Write(_folder, names);

        var output = Fresh();
        var result = ProjectFolder.Build(TestRom.Rom, _folder, output);

        Assert.True(result.ItemNamesApplied, result.OverlayTextError);
        Assert.Equal("", result.OverlayTextError);

        var built = ItemNames.Read(output);
        Assert.Equal("Emerald Leaf", built[38]);
        Assert.Equal("Red Herb", built[39]);
        Assert.Equal("Knife", built[1]);
    }

    /// <summary>A build that changes nothing has to stay byte-identical, overlay included.</summary>
    [RomFact]
    public void AnUneditedBuildDoesNotTouchTheOverlay()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        var output = Fresh();
        var result = ProjectFolder.Build(TestRom.Rom, _folder, output);

        Assert.False(result.ItemNamesApplied);
        Assert.True(result.ByteIdentical);

        var main = OverlayTable.Read(output.Data).First(e => e.Index == 1);
        var retail = OverlayTable.Read(TestRom.Rom.Data).First(e => e.Index == 1);

        Assert.Equal(retail.CompressedSize, main.CompressedSize);
        Assert.True(TestRom.Rom.Data.AsSpan(retail.RomOffset, retail.CompressedSize)
                           .SequenceEqual(output.Data.AsSpan(main.RomOffset, main.CompressedSize)));
    }

    [RomFact]
    public void AFileWithTheWrongNumberOfLinesIsRefusedWithAnExplanation()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        Assert.True(ItemNameFile.TryRead(_folder, out var names, out _));
        names.RemoveAt(names.Count - 1);
        ItemNameFile.Write(_folder, names);

        Assert.False(ItemNameFile.TryRead(_folder, out _, out string error));
        Assert.Contains("must be exactly", error);
    }

    [RomFact]
    public void NamesTooLongToFitFailTheApplyRatherThanTheBuild()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        Assert.True(ItemNameFile.TryRead(_folder, out var names, out _));
        for (int i = 1; i < names.Count; i++) names[i] = new string('W', 40) + i;
        ItemNameFile.Write(_folder, names);

        var output = Fresh();
        var result = ProjectFolder.Build(TestRom.Rom, _folder, output);

        Assert.False(result.ItemNamesApplied);
        Assert.Contains("too many", result.OverlayTextError);

        // The asset region still built, and the names are simply the cart's own.
        Assert.Equal("Green Herb", ItemNames.Read(output)[38]);
    }
}
