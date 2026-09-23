using System;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Project;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

public sealed class ItemTextProjectTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "re2-text-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp */ }
    }

    private RomFile Fresh() => RomFile.FromBytes((byte[])TestRom.Rom.Data.Clone());

    /// <summary>
    /// The file format has to survive every record the cart ships, blank lines and multi-line
    /// records included -- that is the part a marker-delimited format can get wrong.
    /// </summary>
    [RomFact]
    public void EveryRecordSurvivesTheFileFormat()
    {
        var original = ItemMessages.Read(TestRom.Rom);

        Directory.CreateDirectory(_folder);
        ItemTextFile.Write(_folder, original);

        Assert.True(ItemTextFile.TryRead(_folder, out var back, out string error), error);
        Assert.Equal(original, back);
    }

    [RomFact]
    public void RecordsEndingInABlankLineKeepIt()
    {
        var records = ItemMessages.Read(TestRom.Rom);
        records[5] = "ends with a newline\n";
        records[6] = "";
        records[7] = "\n\n";

        Directory.CreateDirectory(_folder);
        ItemTextFile.Write(_folder, records);

        Assert.True(ItemTextFile.TryRead(_folder, out var back, out string error), error);
        Assert.Equal("ends with a newline\n", back[5]);
        Assert.Equal("", back[6]);
        Assert.Equal("\n\n", back[7]);
    }

    [RomFact]
    public void ExtractWritesTheExamineTextAndItReadsBack()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        Assert.True(ItemTextFile.ExistsIn(_folder));
        Assert.True(ItemTextFile.TryRead(_folder, out var records, out string error), error);
        Assert.Equal(ItemMessages.Read(TestRom.Rom), records);
    }

    [RomFact]
    public void AnEditedDescriptionSurvivesABuild()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        Assert.True(ItemTextFile.TryRead(_folder, out var records, out string error), error);
        records[17] = "A very sharp knife.\nBest kept close.<FE>";
        ItemTextFile.Write(_folder, records);

        var output = Fresh();
        var result = ProjectFolder.Build(TestRom.Rom, _folder, output);

        Assert.True(result.ItemTextApplied, result.OverlayTextError);
        Assert.False(result.ItemNamesApplied);                 // untouched, so not rewritten

        var built = ItemMessages.Read(output);
        Assert.Equal("A very sharp knife.\nBest kept close.<FE>", built[17]);
        Assert.Contains("Manufactured by", built[18]);
        Assert.Equal("Green Herb", ItemNames.Read(output)[38]);
    }

    /// <summary>Both blocks are in the same overlay, so editing both must still work in one pass.</summary>
    [RomFact]
    public void ANameAndADescriptionCanChangeTogether()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        Assert.True(ItemNameFile.TryRead(_folder, out var names, out string error), error);
        names[38] = "Emerald Leaf";
        ItemNameFile.Write(_folder, names);

        Assert.True(ItemTextFile.TryRead(_folder, out var records, out error), error);
        records[54] = "Leaves that grow\nin the Raccoon City\nregion.<FE>";
        ItemTextFile.Write(_folder, records);

        var output = Fresh();
        var result = ProjectFolder.Build(TestRom.Rom, _folder, output);

        Assert.True(result.ItemNamesApplied, result.OverlayTextError);
        Assert.True(result.ItemTextApplied, result.OverlayTextError);

        Assert.Equal("Emerald Leaf", ItemNames.Read(output)[38]);
        Assert.Contains("Leaves that grow", ItemMessages.Read(output)[54]);

        var main = OverlayTable.Read(output.Data).First(e => e.Index == 1);
        Assert.True(OverlayTable.TryDecompress(output.Data, main, out _));
    }

    [RomFact]
    public void AMissingMarkerIsRefusedWithAnExplanation()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        string path = ItemTextFile.PathIn(_folder);
        string text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("#5" + Environment.NewLine, ""));

        Assert.False(ItemTextFile.TryRead(_folder, out _, out string error));
        Assert.Contains("#5", error);
    }

    [RomFact]
    public void TextTooLongToFitFailsTheApplyRatherThanTheBuild()
    {
        ProjectFolder.Extract(TestRom.Rom, _folder);

        Assert.True(ItemTextFile.TryRead(_folder, out var records, out _));
        for (int i = 0; i < records.Count; i++) records[i] = new string('W', 200) + i;
        ItemTextFile.Write(_folder, records);

        var output = Fresh();
        var result = ProjectFolder.Build(TestRom.Rom, _folder, output);

        Assert.False(result.ItemTextApplied);
        Assert.Contains("too many", result.OverlayTextError);
        Assert.Contains("A combat knife", ItemMessages.Read(output)[17]);
    }
}
