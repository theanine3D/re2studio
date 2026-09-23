using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

public sealed class OverlayWriterTests
{
    private static (RomFile Rom, OverlayEntry Main) Load()
    {
        var rom = RomFile.FromBytes((byte[])TestRom.Rom.Data.Clone());
        var main = OverlayTable.Read(rom.Data).First(e => e.Index == 1);
        return (rom, main);
    }

    [RomFact]
    public void TheMainOverlayHasRoomToBeRewritten()
    {
        var (rom, main) = Load();
        Assert.True(OverlayWriter.SpareBytes(rom, main) > 0);
    }

    [RomFact]
    public void RewritingAnUnchangedOverlayKeepsItReadable()
    {
        var (rom, main) = Load();

        Assert.True(OverlayTable.TryDecompress(rom.Data, main, out var image));
        Assert.True(OverlayWriter.TryReplace(rom, main, image, out string error), error);

        // Re-read the table: the record's size field has changed.
        var after = OverlayTable.Read(rom.Data).First(e => e.Index == 1);
        Assert.True(OverlayTable.TryDecompress(rom.Data, after, out var back));
        Assert.True(image.SequenceEqual(back));
    }

    [RomFact]
    public void RenamedItemsSurviveTheRoundTripThroughTheCart()
    {
        var (rom, main) = Load();

        var overlay = ModelTextureTable.LoadMainOverlay(rom);
        var names = ItemNames.Read(overlay);
        names[38] = "Emerald Leaf";

        Assert.True(ItemNames.TryWrite(overlay, names, out string error), error);
        Assert.True(OverlayWriter.TryReplace(rom, main, overlay.Data, out error), error);

        var reloaded = ItemNames.Read(ModelTextureTable.LoadMainOverlay(rom));
        Assert.Equal("Emerald Leaf", reloaded[38]);
        Assert.Equal("Knife", reloaded[1]);
        Assert.Equal("Locker Key", reloaded[^1]);
    }

    /// <summary>
    /// The one that matters: the console checks the Adler-32, so a rewritten overlay has to carry a
    /// correct one or it is rejected and the screen goes black.
    /// </summary>
    [RomFact]
    public void ARewrittenOverlayIsStillAValidZlibStream()
    {
        var (rom, main) = Load();

        Assert.True(OverlayTable.TryDecompress(rom.Data, main, out var image));
        Assert.True(OverlayWriter.TryReplace(rom, main, image, out string error), error);

        var after = OverlayTable.Read(rom.Data).First(e => e.Index == 1);
        var stored = rom.Data.AsSpan(after.RomOffset, after.CompressedSize);

        Assert.Equal(0x68, stored[0]);
        Assert.Equal(0xDE, stored[1]);
        Assert.True(Re2.Core.Codecs.Zlib.TryDecompress(stored, out var back));
        Assert.True(image.SequenceEqual(back));
    }

    [RomFact]
    public void AnImageOfTheWrongSizeIsRefused()
    {
        var (rom, main) = Load();

        Assert.False(OverlayWriter.TryReplace(rom, main, new byte[16], out string error));
        Assert.Contains("cannot change size", error);
    }

    [RomFact]
    public void AFailedWriteLeavesTheRomAlone()
    {
        var (rom, main) = Load();
        var before = (byte[])rom.Data.Clone();

        Assert.False(OverlayWriter.TryReplace(rom, main, new byte[16], out _));
        Assert.True(before.SequenceEqual(rom.Data));
    }
}
