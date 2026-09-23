using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>
/// The container the menu screens use, which is not the one every other image uses -- the reason the
/// inventory portraits appear nowhere in a list built by looking for the 0xBE1E magic.
/// </summary>
public class MenuImageTests
{
    private readonly ITestOutputHelper _out;

    public MenuImageTests(ITestOutputHelper output) => _out = output;

    [RomFact]
    public void TheStatusScreensParseAndCarryFourPalettes()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        // 5328 is the Leon scenario's status screen, 5329 the Claire one; the loader picks between
        // them on the scenario flag (FUN_8008b130).
        foreach (int id in new[] { 5328, 5329 })
        {
            var entry = directory.Entries.First(e => e.Index == id);
            Assert.True(directory.TryGetData(rom, entry, out var data));
            Assert.True(MenuImage.TryParse(data, out var image), $"asset {id} did not parse");

            Assert.Equal(256, image!.Width);
            Assert.Equal(256, image.Height);
            Assert.Equal(4, image.Palettes.Count);
            Assert.Equal(2, image.DisplayPalette);

            // Byte-exact, so an edit changes only what it means to.
            Assert.Equal(data, image.Write());

            var rgba = image.ToRgba();
            Assert.Equal(256 * 256 * 4, rgba.Length);
        }
    }

    /// <summary>
    /// How many of these exist, and that the shape check does not claim blobs that are not images.
    /// </summary>
    [RomFact]
    public void TheContainerIsRecognisedWithoutClaimingEverything()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        int found = 0, alsoTexture = 0;
        int first = int.MaxValue, last = 0;

        foreach (var entry in directory.Entries)
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (!MenuImage.TryParse(data, out var image)) continue;

            found++;
            first = System.Math.Min(first, entry.Index);
            last = System.Math.Max(last, entry.Index);

            if (TextureFile.TryParse(data, out _)) alsoTexture++;

            Assert.Equal(data.Length, image!.Write().Length);
        }

        _out.WriteLine($"{found} menu images, asset ids {first}-{last}");

        Assert.InRange(found, 1, 400);
        Assert.Equal(0, alsoTexture);      // the two containers must never both claim a blob
    }

    /// <summary>
    /// Export through a palette, import back through the same one, and the picture must survive
    /// exactly.
    /// </summary>
    [RomFact]
    public void ExportAndImportThroughTheSamePaletteRoundTrips()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        var entry = directory.Entries.First(e => e.Index == 5328);
        Assert.True(directory.TryGetData(rom, entry, out var data));
        Assert.True(MenuImage.TryParse(data, out var image));

        for (int palette = 0; palette < image!.Palettes.Count; palette++)
        {
            var exported = image.ToRgba(palette);
            var back = image.WithPixels(exported, image.Width, image.Height, palette);

            // Indices may differ where a palette holds the same colour twice, so compare what is
            // actually seen rather than the indices.
            Assert.Equal(exported, back.ToRgba(palette));
        }
    }

    /// <summary>
    /// The failure the palette choice exists to prevent: matching against a palette the picture did
    /// not come from mangles it even though nothing was edited.
    /// </summary>
    [RomFact]
    public void MatchingAgainstTheWrongPaletteVisiblyDamagesTheImage()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        var entry = directory.Entries.First(e => e.Index == 5328);
        Assert.True(directory.TryGetData(rom, entry, out var data));
        Assert.True(MenuImage.TryParse(data, out var image));

        var exported = image!.ToRgba(0);

        var right = image.WithPixels(exported, image.Width, image.Height, 0).ToRgba(0);
        var wrong = image.WithPixels(exported, image.Width, image.Height, 2).ToRgba(0);

        int wrongPixels = 0;
        for (int i = 0; i < exported.Length; i += 4)
            if (wrong[i] != exported[i] || wrong[i + 1] != exported[i + 1] || wrong[i + 2] != exported[i + 2])
                wrongPixels++;

        _out.WriteLine($"matched against palette 2, displayed through 0: {wrongPixels:N0} pixels differ");

        Assert.Equal(exported, right);
        Assert.True(wrongPixels > 1000, "the wrong palette was expected to do visible damage");
    }

    /// <summary>
    /// A palette exported as a strip and imported back unchanged must leave the screen exactly as it
    /// was -- the round trip has to be lossless, or every edit drags the untouched colours with it.
    /// </summary>
    [RomFact]
    public void APaletteSurvivesBeingExportedAndImportedUnchanged()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        var entry = directory.Entries.First(e => e.Index == 5328);
        Assert.True(directory.TryGetData(rom, entry, out var data));
        Assert.True(MenuImage.TryParse(data, out var image));

        for (int palette = 0; palette < image!.Palettes.Count; palette++)
        {
            var strip = image.PaletteToRgba(palette);
            var back = image.WithPalette(palette, strip);

            Assert.Equal(data, back.Write());
        }
    }

    /// <summary>Replacing a palette changes what the pixels resolve to and nothing else.</summary>
    [RomFact]
    public void ReplacingAPaletteLeavesEveryPixelIndexAlone()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        var entry = directory.Entries.First(e => e.Index == 5328);
        Assert.True(directory.TryGetData(rom, entry, out var data));
        Assert.True(MenuImage.TryParse(data, out var image));

        // A colour the screen does not contain: solid magenta, everywhere.
        var strip = new byte[image!.Colours * 4];
        for (int i = 0; i < image.Colours; i++)
        {
            strip[i * 4] = 255; strip[i * 4 + 1] = 0; strip[i * 4 + 2] = 255; strip[i * 4 + 3] = 255;
        }

        var changed = image.WithPalette(2, strip);

        Assert.Equal(image.Indices, changed.Indices);
        Assert.Equal(image.Palettes[0], changed.Palettes[0]);
        Assert.Equal(image.Palettes[1], changed.Palettes[1]);
        Assert.Equal(image.Palettes[3], changed.Palettes[3]);

        // Every opaque pixel now reads as that colour under palette 2, and only under palette 2.
        var under2 = changed.ToRgba(2);
        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(255, under2[i * 4]);
            Assert.Equal(0, under2[i * 4 + 1]);
            Assert.Equal(255, under2[i * 4 + 2]);
        }

        Assert.Equal(image.ToRgba(0), changed.ToRgba(0));
    }
}
