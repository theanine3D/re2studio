using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public sealed class ItemNameTests
{
    [Theory]
    [InlineData("Green Herb")]
    [InlineData("F. Aid Spray")]
    [InlineData("Colt S.A.A.")]
    [InlineData("Chemical FR-W09")]
    [InlineData("Secretary's diary A")]
    [InlineData("Bomb <EE>6 Det.")]
    public void TextRoundTrips(string text)
    {
        Assert.True(ItemText.TryEncode(text, out var codes, out string error), error);
        Assert.Equal(text, ItemText.Decode(codes));
    }

    [Fact]
    public void TextRefusesCharactersTheGameCannotDraw()
    {
        Assert.False(ItemText.TryEncode("Grün Herb", out _, out string error));
        Assert.Contains("no character", error);
    }

    [Fact]
    public void TextRefusesAMalformedEscape()
    {
        Assert.False(ItemText.TryEncode("Bomb <EEE> Det.", out _, out string error));
        Assert.Contains("<XX>", error);
    }

    [RomFact]
    public void ReadsTheNamesTheGameShows()
    {
        var names = ItemNames.Read(TestRom.Rom);

        Assert.Equal(ItemNames.Count, names.Count);
        Assert.Equal("", names[0]);                      // no item
        Assert.Equal("Knife", names[1]);
        Assert.Equal("Hand Gun", names[2]);
        Assert.Equal("Green Herb", names[38]);
        Assert.Equal("F. Aid Spray", names[35]);
        Assert.Equal("Ink Ribbon", names[30]);
        Assert.Equal("Blue Card Key", names[53]);
    }

    /// <summary>
    /// Writing back what was read has to reproduce the cart's own bytes, or an untouched edit would
    /// quietly change the ROM.
    /// </summary>
    [RomFact]
    public void WritingBackWhatWasReadChangesNothingThatMatters()
    {
        var overlay = ModelTextureTable.LoadMainOverlay(TestRom.Rom);
        var names = ItemNames.Read(overlay);

        Assert.True(ItemNames.TryWrite(overlay, names, out string error), error);
        Assert.Equal(names, ItemNames.Read(overlay));
    }

    [RomFact]
    public void RenamingAnItemReadsBackRenamed()
    {
        var overlay = ModelTextureTable.LoadMainOverlay(TestRom.Rom);
        var names = ItemNames.Read(overlay);

        names[38] = "Emerald Leaf";
        Assert.True(ItemNames.TryWrite(overlay, names, out string error), error);

        var back = ItemNames.Read(overlay);
        Assert.Equal("Emerald Leaf", back[38]);
        Assert.Equal("Red Herb", back[39]);               // its neighbours moved but still read
        Assert.Equal("Knife", back[1]);
    }

    [RomFact]
    public void TheCartsOwnNamesLeaveRoomToSpare()
    {
        var names = ItemNames.Read(TestRom.Rom);
        int used = ItemNames.Measure(names);

        Assert.InRange(used, 1, ItemNames.Capacity);
    }

    [RomFact]
    public void NamesThatDoNotFitAreRefusedRatherThanTruncated()
    {
        var names = ItemNames.Read(TestRom.Rom);

        for (int i = 1; i < names.Count; i++) names[i] = new string('W', 60) + i;

        Assert.False(ItemNames.TryBuild(names, out _, out _, out string error));
        Assert.Contains("too many", error);
    }

    /// <summary>Identical names share one copy, which is where the headroom for longer ones comes from.</summary>
    [RomFact]
    public void RepeatedNamesAreStoredOnce()
    {
        var names = ItemNames.Read(TestRom.Rom);

        var uniform = new List<string>(names);
        for (int i = 1; i < uniform.Count; i++) uniform[i] = "Herb";

        // A separator, "Herb", and the separator that closes it -- once, not once per entry.
        Assert.Equal(1 + 4 + 1, ItemNames.Measure(uniform));
        Assert.True(ItemNames.Measure(names) > ItemNames.Measure(uniform));
    }
}
