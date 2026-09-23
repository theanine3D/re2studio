using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

public class AssetDirectoryTests
{
    [RomFact]
    public void LocatesTheDirectoryThroughTheOverlayTable()
    {
        var dir = AssetDirectory.Read(TestRom.Rom);

        Assert.Equal(0xC8624, dir.AssetBaseRomOffset);
        Assert.Equal(8091, dir.DeclaredFileCount);
        Assert.Equal(7978, dir.Entries.Count);
        Assert.Equal(113, dir.NullSlotCount);
        Assert.Contains("Sep 28", dir.BuildStamp);
    }

    /// <summary>
    /// The table sits at the very front of the asset blob and the first file begins immediately
    /// after it, so the first real record's offset must equal the table's own size.
    /// </summary>
    [RomFact]
    public void FirstFileStartsRightAfterTheTable()
    {
        var dir = AssetDirectory.Read(TestRom.Rom);
        int tableSize = AssetDirectory.HeaderSize + dir.DeclaredFileCount * AssetDirectory.RecordSize;
        Assert.Equal(tableSize, dir.Entries[0].Offset);
    }

    /// <summary>
    /// Decisive cross-check: the 1,227 backgrounds were found purely by scanning for JFIF markers, with
    /// no knowledge of the directory.
    /// </summary>
    [RomFact]
    public void EveryBackgroundIsADirectoryEntry()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);
        var backgrounds = BackgroundIndex.Build(rom);

        foreach (var bg in backgrounds.Backgrounds)
        {
            var entry = dir.FindByRomOffset(bg.Offset);
            Assert.True(entry is not null, $"background {bg.Index} at 0x{bg.Offset:X7} is not in the directory");
            Assert.Equal(bg.Length, entry!.StoredSize);
            Assert.Equal(AssetKind.Stored, entry.Kind);
        }
    }

    [RomFact]
    public void EveryDeflateAssetInflatesToItsDeclaredSize()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);
        var deflate = dir.Entries.Where(e => e.Kind == AssetKind.Deflate).ToList();

        Assert.Equal(4852, deflate.Count);
        foreach (var entry in deflate)
        {
            Assert.True(dir.TryGetData(rom, entry, out var data), $"asset {entry.Index} failed to inflate");
            Assert.Equal(entry.DecompressedSize, data.Length);
        }
    }

    [RomFact]
    public void StoredAssetsDeclareTheirOwnSize()
    {
        var dir = AssetDirectory.Read(TestRom.Rom);
        foreach (var entry in dir.Entries.Where(e => e.Kind == AssetKind.Stored))
            Assert.Equal(entry.StoredSize, entry.DecompressedSize);
    }

    [RomFact]
    public void EveryEntryCarriesAKnownCodec()
    {
        var dir = AssetDirectory.Read(TestRom.Rom);
        var kinds = dir.Entries.Select(e => e.Kind).Distinct().OrderBy(k => (int)k).ToArray();
        Assert.Equal(new[] { AssetKind.Stored, AssetKind.Rle, AssetKind.Rle16, AssetKind.Deflate }, kinds);
    }

    /// <summary>Assets must not overlap.</summary>
    [RomFact]
    public void AssetsDoNotOverlap()
    {
        var dir = AssetDirectory.Read(TestRom.Rom);
        var ordered = dir.Entries.OrderBy(e => e.RomOffset).ToList();

        for (int i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            if (previous.RomOffset == ordered[i].RomOffset) continue; // shared/aliased files exist
            int endOfPrevious = previous.TrailerRomOffset + AssetDirectory.TrailerSize;
            Assert.True(endOfPrevious <= ordered[i].RomOffset,
                $"asset {previous.Index} runs to 0x{endOfPrevious:X7}, past asset {ordered[i].Index} at 0x{ordered[i].RomOffset:X7}");
        }
    }

    /// <summary>The directory should account for essentially the whole asset blob.</summary>
    [RomFact]
    public void DirectoryCoversTheAssetRegion()
    {
        var dir = AssetDirectory.Read(TestRom.Rom);
        var ordered = dir.Entries.OrderBy(e => e.RomOffset).ToList();

        int last = ordered.Max(e => e.TrailerRomOffset + AssetDirectory.TrailerSize);
        Assert.True(last >= Re2RomMap.LastUsedByte - 0x2000,
            $"directory ends at 0x{last:X7} but the ROM has data to 0x{Re2RomMap.LastUsedByte:X7}");

        long accounted = ordered.Sum(e => (long)(e.PaddedSize + AssetDirectory.TrailerSize));
        long span = last - dir.Entries.Min(e => e.RomOffset);
        Assert.True(accounted >= span * 0.99,
            $"only {accounted:N0} of {span:N0} bytes accounted for");
    }
}
