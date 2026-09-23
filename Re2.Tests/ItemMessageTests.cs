using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

public sealed class ItemMessageTests
{
    private readonly ITestOutputHelper _out;
    public ItemMessageTests(ITestOutputHelper o) => _out = o;

    [RomFact]
    public void ReadsTheExamineTextTheGameShows()
    {
        var text = ItemMessages.Read(TestRom.Rom);

        Assert.Equal(ItemMessages.Count, text.Count);
        Assert.Contains("Will you take the", text[0]);
        Assert.Contains("You can't carry any", text[1]);
        Assert.Contains("A combat knife", text[17]);
        Assert.Contains("Manufactured by", text[18]);
        Assert.Contains("parabellum rounds", text[18]);
        Assert.Contains("Raccoon", text[54]);
    }

    /// <summary>A weapon's record holds its name, its description and a second page.</summary>
    [RomFact]
    public void AWeaponRecordKeepsItsPageBreak()
    {
        var text = ItemMessages.Read(TestRom.Rom);

        Assert.StartsWith("H<EE>6K VP70", text[18]);
        Assert.Contains("<FD>", text[18]);              // the page break
        Assert.Contains("<FE>", text[18]);              // the end of the record
        Assert.Contains("\n", text[18]);                // line breaks read as newlines
    }

    [RomFact]
    public void EveryRecordSurvivesARoundTrip()
    {
        var overlay = ModelTextureTable.LoadMainOverlay(TestRom.Rom);
        var text = ItemMessages.Read(overlay);

        Assert.True(ItemMessages.TryWrite(overlay, text, out string error), error);

        var back = ItemMessages.Read(overlay);
        for (int i = 0; i < text.Count; i++)
            if (text[i] != back[i])
                _out.WriteLine($"[{i}] was {text[i].Length} chars {Esc(text[i])}" +
                               $" | now {back[i].Length} chars {Esc(back[i])}");

        Assert.Equal(text, back);
    }

    private static string Esc(string s) => s.Replace('\n', '~');

    [RomFact]
    public void EditingOneRecordLeavesTheOthersReadable()
    {
        var overlay = ModelTextureTable.LoadMainOverlay(TestRom.Rom);
        var text = ItemMessages.Read(overlay);

        text[17] = "A very sharp knife.\nIt has seen better\ndays.<FE>";
        Assert.True(ItemMessages.TryWrite(overlay, text, out string error), error);

        var back = ItemMessages.Read(overlay);
        Assert.Equal(text[17], back[17]);
        Assert.Contains("Manufactured by", back[18]);
        Assert.Contains("Will you take the", back[0]);
    }

    [RomFact]
    public void TheCartsOwnTextLeavesRoomToSpare()
    {
        var text = ItemMessages.Read(TestRom.Rom);
        int used = ItemMessages.Measure(text);

        _out.WriteLine($"{used:N0} of {ItemMessages.Capacity:N0} bytes, " +
                       $"{ItemMessages.Capacity - used:N0} spare");

        Assert.InRange(used, 1, ItemMessages.Capacity);
    }

    [RomFact]
    public void TextThatDoesNotFitIsRefusedRatherThanTruncated()
    {
        var text = ItemMessages.Read(TestRom.Rom);
        for (int i = 0; i < text.Count; i++) text[i] = new string('W', 200) + i;

        Assert.False(ItemMessages.TryBuild(text, out _, out _, out string error));
        Assert.Contains("too many", error);
    }

    [RomFact]
    public void CharactersTheGameCannotDrawAreRefused()
    {
        var text = ItemMessages.Read(TestRom.Rom);
        text[17] = "A combat knife ç";

        Assert.False(ItemMessages.TryBuild(text, out _, out _, out string error));
        Assert.Contains("no character", error);
    }

    /// <summary>The pairing the editor links the two lists by.</summary>
    [RomFact]
    public void EachItemPairsWithItsOwnDescription()
    {
        var names = ItemNames.Read(TestRom.Rom);
        var records = ItemMessages.Read(TestRom.Rom);

        void Pair(int id, string name, string mentions)
        {
            Assert.Equal(name, names[id]);

            int record = ItemMessages.RecordForItem(id);
            Assert.InRange(record, 0, records.Count - 1);
            Assert.Contains(mentions, records[record]);
            Assert.Equal(id, ItemMessages.ItemForRecord(record));
        }

        Pair(1, "Knife", "combat knife");
        Pair(30, "Ink Ribbon", "type in my");
        Pair(35, "F. Aid Spray", "restore my vitality");
        Pair(38, "Green Herb", "Raccoon");
        Pair(47, "Lighter", "oil lighter");

        // Repeated names against distinct descriptions: a misaligned offset would not survive these.
        Pair(89, "Precinct Key", "spade");
        Pair(90, "Precinct Key", "diamond");
        Pair(91, "Precinct Key", "heart");
        Pair(92, "Precinct Key", "club");
        Pair(99, "Platform Key", "train");
    }

    /// <summary>
    /// The descriptions stop exactly where the named items do, which is the check that the offset is
    /// not merely plausible in the middle of the range.
    /// </summary>
    [RomFact]
    public void ItemsWithNoDescriptionSaySo()
    {
        var names = ItemNames.Read(TestRom.Rom);

        Assert.Equal(-1, ItemMessages.RecordForItem(0));          // "no item"
        Assert.Equal(115, ItemMessages.RecordForItem(99));        // the last description there is
        Assert.Equal(-1, ItemMessages.RecordForItem(100));

        // And that is where the real items stop: the cart spells the empty slots out.
        Assert.Equal("no item", names[100]);
        Assert.Equal("Platform Key", names[99]);

        // Prompts are not descriptions of anything.
        Assert.Equal(-1, ItemMessages.ItemForRecord(0));
        Assert.Equal(-1, ItemMessages.ItemForRecord(16));
        Assert.Equal(1, ItemMessages.ItemForRecord(17));
    }
}
