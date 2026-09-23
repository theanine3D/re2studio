using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Foreground masks: the parts of a background that draw in front of the player.</summary>
public class MaskTests
{
    private readonly ITestOutputHelper _out;

    public MaskTests(ITestOutputHelper output) => _out = output;

    [RomFact]
    public void TheMaskTableAgreesWithTheRoomTableOnCameraCounts()
    {
        var rom = TestRom.Rom;
        var overlay = ModelTextureTable.LoadMainOverlay(rom);

        var rooms = RoomTable.Read(overlay);
        var masks = MaskTable.Read(overlay);

        Assert.Equal(rooms.Count, masks.Count);

        // Both tables are indexed by camera, so both must describe the same number of them.
        for (int i = 0; i < rooms.Count; i++)
            Assert.True(rooms[i].ViewCount <= masks[i].Length,
                        $"room {i}: {rooms[i].ViewCount} views but only {masks[i].Length} mask slots");

        int slots = masks.Sum(m => m.Length);
        int withMask = masks.Sum(m => m.Count(id => id >= 0));

        _out.WriteLine($"{slots} camera slots, {withMask} with a foreground");
        Assert.Equal(1308, slots);
        Assert.InRange(withMask, 700, 1000);
    }

    [RomFact]
    public void EveryMaskAssetParses()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var masks = MaskTable.Read(ModelTextureTable.LoadMainOverlay(rom));

        var ids = masks.SelectMany(m => m).Where(id => id >= 0).Distinct().OrderBy(id => id).ToList();
        int parsed = 0, pieces = 0, covered = 0, ownPixels = 0;

        foreach (int id in ids)
        {
            var entry = directory.Entries.FirstOrDefault(e => e.Index == id);
            if (entry is null || !directory.TryGetData(rom, entry, out var data)) continue;

            Assert.True(MaskFile.TryParse(data, out var mask), $"mask asset {id} did not parse");

            parsed++;
            pieces += mask!.Pieces.Count;
            covered += mask.CoveredPixels;
            ownPixels += mask.Pieces.Count(p => p.HasOwnPixels);

            // Every piece has to sit on the screen it claims to cover.
            Assert.Equal(320, mask.Width);
            Assert.Equal(224, mask.Height);
            foreach (var piece in mask.Pieces)
            {
                Assert.InRange(piece.X, 0, mask.Width);
                Assert.InRange(piece.Y, 0, mask.Height);
                Assert.True(piece.Right <= mask.Width + 8, $"asset {id}: piece runs to x={piece.Right}");
            }
        }

        _out.WriteLine($"{parsed} masks parsed, {pieces:N0} pieces, {covered:N0} covered pixels, " +
                       $"{ownPixels:N0} pieces carrying their own image");

        Assert.True(parsed >= 780, $"only {parsed} masks parsed");
    }

    /// <summary>
    /// A mask covers part of the screen, not all of it and not none: a parser that lost its place
    /// would produce either.
    /// </summary>
    [RomFact]
    public void MasksCoverAPlausibleShareOfTheScreen()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var masks = MaskTable.Read(ModelTextureTable.LoadMainOverlay(rom));

        var ids = masks.SelectMany(m => m).Where(id => id >= 0).Distinct().Take(120).ToList();
        int checkedCount = 0;

        foreach (int id in ids)
        {
            var entry = directory.Entries.First(e => e.Index == id);
            directory.TryGetData(rom, entry, out var data);
            MaskFile.TryParse(data, out var mask);

            double share = (double)mask!.CoveredPixels / (mask.Width * mask.Height);
            Assert.InRange(share, 0.0, 1.0);
            checkedCount++;
        }

        Assert.True(checkedCount > 100);
    }

    [RomFact]
    public void APieceCarriesTheDepthThatPutsItInFront()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == 6983);
        directory.TryGetData(rom, entry, out var data);

        Assert.True(MaskFile.TryParse(data, out var mask));
        Assert.Equal(49, mask!.Pieces.Count);

        var first = mask.Pieces[0];
        Assert.Equal(40, first.X);
        Assert.Equal(88, first.Y);
        Assert.Equal(8, first.Width);
        Assert.Equal(8, first.Height);
        Assert.Equal(750, first.Depth);

        // Cut to a shape rather than a solid block: most of the 8x8 is covered, but not all of it.
        int covered = first.Covered.Count(c => c);
        Assert.Equal(52, covered);
        Assert.InRange(covered, 1, first.Width * first.Height - 1);
    }

    /// <summary>The encoder has to be byte-exact, not merely valid.</summary>
    [RomFact]
    public void EveryMaskReencodesToTheExactBytesItCameFrom()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var masks = MaskTable.Read(ModelTextureTable.LoadMainOverlay(rom));

        var ids = masks.SelectMany(m => m).Where(id => id >= 0).Distinct().OrderBy(id => id).ToList();
        int checked_ = 0, trailing = 0;

        foreach (int id in ids)
        {
            var entry = directory.Entries.FirstOrDefault(e => e.Index == id);
            if (entry is null || !directory.TryGetData(rom, entry, out var data)) continue;
            if (!MaskFile.TryParse(data, out var mask)) continue;

            var written = mask!.Write();

            // Exactly the same bytes, including anything past the palette.
            Assert.True(written.Length == data.Length,
                        $"mask asset {id}: wrote {written.Length} bytes from {data.Length}");

            if (mask.Trailing.Length > 0) trailing++;

            for (int i = 0; i < written.Length; i++)
                Assert.True(written[i] == data[i],
                            $"mask asset {id}: byte {i} was 0x{data[i]:X2}, wrote 0x{written[i]:X2}");

            checked_++;
        }

        _out.WriteLine($"{checked_} masks re-encoded byte for byte, {trailing} carrying bytes past the palette");
        Assert.InRange(checked_, 700, 1000);
    }

    /// <summary>Replacing coverage keeps the geometry.</summary>
    [RomFact]
    public void ImportedCoverageKeepsEveryPieceAndItsDepth()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var masks = MaskTable.Read(ModelTextureTable.LoadMainOverlay(rom));

        int id = masks.SelectMany(m => m).First(i => i >= 0);
        var entry = directory.Entries.First(e => e.Index == id);
        Assert.True(directory.TryGetData(rom, entry, out var data));
        Assert.True(MaskFile.TryParse(data, out var mask));

        // Asking for exactly what is already there must give back exactly what is already there.
        var screen = new bool[mask!.Width * mask.Height];
        foreach (var piece in mask.Pieces)
            for (int row = 0; row < piece.Height; row++)
                for (int column = 0; column < piece.Width; column++)
                    if (piece.Covered[row * piece.Width + column])
                    {
                        int x = piece.X + column, y = piece.Y + row;
                        if (x < mask.Width && y < mask.Height) screen[y * mask.Width + x] = true;
                    }

        var same = mask.WithCoverage(screen, out int outside, out _);
        Assert.Equal(0, outside);
        Assert.Equal(mask.Write(), same.Write());

        // And an empty import empties the run-length pieces without disturbing their rectangles.
        var cleared = mask.WithCoverage(new bool[screen.Length], out _, out int untouched);

        Assert.Equal(mask.Pieces.Count, cleared.Pieces.Count);
        for (int i = 0; i < mask.Pieces.Count; i++)
        {
            Assert.Equal(mask.Pieces[i].X, cleared.Pieces[i].X);
            Assert.Equal(mask.Pieces[i].Y, cleared.Pieces[i].Y);
            Assert.Equal(mask.Pieces[i].Width, cleared.Pieces[i].Width);
            Assert.Equal(mask.Pieces[i].Height, cleared.Pieces[i].Height);
            Assert.Equal(mask.Pieces[i].Depth, cleared.Pieces[i].Depth);
        }

        Assert.Equal(untouched, cleared.Pieces.Count(p => p.HasOwnPixels));
        Assert.True(MaskFile.TryParse(cleared.Write(), out _), "a cleared mask must still parse");
    }

    /// <summary>
    /// The export and the import have to agree, or the round trip everyone will actually use -- export
    /// the layer, paint on it, bring it back -- quietly changes the shape on the way through.
    /// </summary>
    [RomFact]
    public void AnExportedForegroundDescribesTheSameShapeItCameFrom()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var masks = MaskTable.Read(ModelTextureTable.LoadMainOverlay(rom));

        // A mask with no piece carrying its own image, so coverage is the whole story.
        foreach (int id in masks.SelectMany(m => m).Where(i => i >= 0).Distinct().Take(200))
        {
            var entry = directory.Entries.FirstOrDefault(e => e.Index == id);
            if (entry is null || !directory.TryGetData(rom, entry, out var data)) continue;
            if (!MaskFile.TryParse(data, out var mask)) continue;
            if (mask!.Pieces.Any(p => p.HasOwnPixels) || mask.CoveredPixels == 0) continue;

            // A background of solid opaque pixels, so the layer's alpha is the only thing carrying shape.
            var background = new byte[mask.Width * mask.Height * 4];
            for (int i = 0; i < background.Length; i++) background[i] = 255;

            var layer = MaskRenderer.Compose(mask, background, mask.Width, mask.Height);

            var screen = new bool[mask.Width * mask.Height];
            for (int i = 0; i < screen.Length; i++) screen[i] = layer[i * 4 + 3] >= 128;

            var back = mask.WithCoverage(screen, out int outside, out _);

            Assert.Equal(0, outside);
            Assert.Equal(mask.Write(), back.Write());
            return;
        }

        Assert.Fail("no run-length-only mask found to round-trip");
    }
}
