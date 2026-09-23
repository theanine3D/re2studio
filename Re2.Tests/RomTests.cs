using System.Linq;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

public class RomTests
{
    [RomFact]
    public void ReadsHeader()
    {
        var rom = TestRom.Rom;
        Assert.Equal(Re2RomMap.ExpectedLength, rom.Length);
        Assert.Equal("Resident Evil II", rom.InternalName);
        Assert.Equal("NREE", rom.GameCode);
        Assert.Equal('N', rom.MediaFormat);
        Assert.Equal("RE", rom.CartridgeId);
        Assert.Equal('E', rom.RegionCode);
        Assert.Equal(0x80000480u, rom.EntryPoint);
    }

    [RomFact]
    public void DetectsCicAndValidatesChecksum()
    {
        var rom = TestRom.Rom;
        Assert.Equal(CicChip.Cic6102, rom.Cic);
        Assert.True(rom.VerifyCrc(), "retail ROM header checksum should validate");
    }

    [RomFact]
    public void RecomputedChecksumMatchesTheOriginal()
    {
        var rom = TestRom.Rom;
        uint before1 = rom.Crc1, before2 = rom.Crc2;
        rom.FixCrc();
        Assert.Equal(before1, rom.Crc1);
        Assert.Equal(before2, rom.Crc2);
    }

    [RomFact]
    public void EditingCodeInvalidatesThenRepairsChecksum()
    {
        var rom = RomFile.Load(TestRom.Path!);
        rom.Data[0x2000] ^= 0xFF;
        Assert.False(rom.VerifyCrc());
        rom.FixCrc();
        Assert.True(rom.VerifyCrc());
    }

    [Fact]
    public void ComputesContainerPacking()
    {
        // A file at 0xFD00 of size 0x319 is followed by 8 bytes, then 2-byte alignment.
        Assert.Equal(0x10022, Re2RomMap.NextFileOffset(0xFD00, 0x319));
        Assert.Equal(0x1007A, Re2RomMap.NextFileOffset(0x10022, 0x50));
        Assert.Equal(0x10B18, Re2RomMap.NextFileOffset(0x1007A, 0xA95));
    }

    [Fact]
    public void RegionsAreContiguousAndCoverTheRom()
    {
        var regions = Re2RomMap.Regions;
        Assert.Equal(0, regions[0].Start);
        Assert.Equal(Re2RomMap.ExpectedLength, regions[^1].End);
        for (int i = 1; i < regions.Count; i++)
            Assert.Equal(regions[i - 1].End, regions[i].Start);
    }
}

public class CodeSegmentTests
{
    [RomFact]
    public void FindsTheFullDeflateChain()
    {
        var rom = TestRom.Rom;
        var segments = CodeSegments.Scan(rom.Data, Re2RomMap.CodeChainStart, Re2RomMap.CodeChainLimit);

        Assert.Equal(36, segments.Count);
        Assert.Equal(Re2RomMap.CodeChainStart, segments[0].RomOffset);
        Assert.Equal(0xB624D, segments[^1].RomEnd);
        Assert.Equal(662862, segments.Sum(s => s.CompressedSize));
        Assert.Equal(1877938, segments.Sum(s => s.DecompressedSize));
    }

    [RomFact]
    public void SegmentsDoNotOverlapAndAdvanceInOrder()
    {
        var segments = CodeSegments.Scan(TestRom.Rom.Data, Re2RomMap.CodeChainStart, Re2RomMap.CodeChainLimit);
        for (int i = 1; i < segments.Count; i++)
        {
            Assert.True(segments[i].RomOffset >= segments[i - 1].RomEnd);
            Assert.True(segments[i].RomOffset - segments[i - 1].RomEnd < CodeSegments.MaxPadding);
        }
    }

    [RomFact]
    public void DecompressedCodeContainsExpectedLibrarySignatures()
    {
        var rom = TestRom.Rom;
        var segments = CodeSegments.Scan(rom.Data, Re2RomMap.CodeChainStart, Re2RomMap.CodeChainLimit);
        string text = System.Text.Encoding.ASCII.GetString(CodeSegments.DecompressAll(rom.Data, segments));

        // libjpeg: the backgrounds are baseline JPEG, decoded on the CPU by IJG v5.
        Assert.Contains("Copyright (C) 1994, Thomas G. Lane", text);
        Assert.Contains("Not a JPEG file: starts with", text);

        // Capcom's original PS1 asset formats survived the port.
        Assert.Contains("d:/bio2/room/emd/em000.emd", text);
        Assert.Contains("d:/bio2/room/emd/em000.tim", text);

        // Asset-type tags used by the loader.
        Assert.Contains("SND_PROJ", text);
        Assert.Contains("PL EMD AREA", text);
    }

    [RomFact]
    public void EachSegmentRoundTripsThroughOurDeflate()
    {
        var rom = TestRom.Rom;
        var segments = CodeSegments.Scan(rom.Data, Re2RomMap.CodeChainStart, Re2RomMap.CodeChainLimit);

        foreach (var segment in segments)
        {
            var original = CodeSegments.Decompress(rom.Data, segment);
            var repacked = Re2.Core.Codecs.RawDeflate.Compress(original);
            Assert.True(Re2.Core.Codecs.Inflate.TryInflateRaw(repacked, out var restored, out _));
            Assert.Equal(original, restored);
        }
    }
}

public class OverlayTableTests
{
    [RomFact]
    public void FindsTheTableAndItsMagic()
    {
        var rom = TestRom.Rom;
        Assert.True(OverlayTable.IsPresent(rom.Data));
        Assert.Equal(66, OverlayTable.Read(rom.Data).Count);
    }

    /// <summary>Entry 1 is the main binary.</summary>
    [RomFact]
    public void MainBinaryEntryMatchesTheBootCode()
    {
        var main = OverlayTable.Read(TestRom.Rom.Data)[0];
        Assert.Equal(1, main.Index);
        Assert.Equal(0x14420, main.RomOffset);
        Assert.Equal(0x80018A80u, main.LoadAddress);
        Assert.Equal(0x80136D90u, main.EndAddress);
        Assert.Equal(1172240, main.DecompressedSize);
    }

    /// <summary>
    /// Independent cross-check: the streams found by structurally chaining deflate must line up
    /// with what the game's table declares, in both position and decompressed size.
    /// </summary>
    [RomFact]
    public void TableAgreesWithTheStructuralDeflateScan()
    {
        var rom = TestRom.Rom;
        var scanned = CodeSegments.Scan(rom.Data, Re2RomMap.CodeChainStart, Re2RomMap.CodeChainLimit)
            .ToDictionary(s => s.RomOffset, s => s.DecompressedSize);

        int matched = 0;
        foreach (var entry in OverlayTable.Read(rom.Data))
        {
            // Empty records point at the same payload as the real entry that follows them.
            if (entry.IsEmpty) continue;
            if (!scanned.TryGetValue(entry.PayloadOffset, out int decompressed)) continue;
            Assert.Equal(entry.DecompressedSize, decompressed);
            matched++;
        }

        Assert.True(matched >= 35, $"only {matched} table entries lined up with scanned streams");
    }

    [RomFact]
    public void EveryDeflateEntryDecompressesToItsDeclaredSize()
    {
        var rom = TestRom.Rom;
        var usable = OverlayTable.DeflateEntries(rom.Data, OverlayTable.Read(rom.Data));

        Assert.Equal(35, usable.Count);
        foreach (var entry in usable)
        {
            Assert.True(OverlayTable.TryDecompress(rom.Data, entry, out var data));
            Assert.Equal(entry.DecompressedSize, data.Length);
        }
    }

    [RomFact]
    public void OverlaysShareLoadSlots()
    {
        // Confirms these are true runtime overlays, which is why Ghidra needs overlay blocks.
        var usable = OverlayTable.DeflateEntries(TestRom.Rom.Data, OverlayTable.Read(TestRom.Rom.Data));
        var busiest = usable.GroupBy(e => e.LoadAddress).OrderByDescending(g => g.Count()).First();
        Assert.Equal(0x80161CF0u, busiest.Key);
        Assert.Equal(28, busiest.Count());
    }
}
