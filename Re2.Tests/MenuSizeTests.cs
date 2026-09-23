using System.Linq;
using Re2.Core.Codecs;
using Re2.Core.Formats;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>Edited menu screens must not balloon in the ROM.</summary>
public sealed class MenuSizeTests
{
    [RomFact]
    public void LargeMenuScreensStayCompressedAndReadable()
    {
        using var session = new RomSession(TestRom.Path!);
        foreach (var (entry, image) in session.MenuImages.Where(m => m.Image.Width * m.Image.Height > 16 * 1024))
        {
            var decoded = image.Write();
            var stored = Zlib.Compress(decoded);

            // Within a tenth of the original, rather than four to ten times it.
            Assert.True(stored.Length < entry.StoredSize * 11 / 10,
                $"asset {entry.Index}: {stored.Length:N0} vs original {entry.StoredSize:N0}");
            Assert.True(Zlib.TryDecompress(stored, out var back));
            Assert.Equal(decoded, back);
            Assert.True(Zlib.FitsWindow(stored.AsSpan(Zlib.HeaderSize, stored.Length - Zlib.HeaderSize - Zlib.ChecksumSize)));
        }
    }

    [RomFact]
    public void ImportsAreFittedToTheOriginalSize()
    {
        using var session = new RomSession(TestRom.Path!);
        foreach (int id in new[] { 5320, 5328, 5449 })
        {
            var image = session.MenuImages.First(m => m.Entry.Index == id).Image;
            session.TryGetCartAsset(id, out _, out int budget);
            int p = image.DisplayPalette;

            // A picture with far more colours than the screen had: a gradient across the whole thing.
            var rgba = image.ToRgba(p);
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] ^= (byte)(i / 4 % image.Width * 255 / image.Width);
                rgba[i + 3] = 255;
            }

            var plain = AssetIo.FitMenuToOriginal(session, id, c => image.WithPixels(
                c >= AssetIo.FullColour ? rgba : MenuPaletteBuilder.Reduce(rgba, c), image.Width, image.Height, p));
            Assert.True(Zlib.Compress(plain.Bytes).Length <= budget, plain.Report);

            var fresh = AssetIo.FitMenuToOriginal(session, id, c =>
                image.WithPalette(p, MenuPaletteBuilder.Build(rgba, 256, c).PaletteRgba)
                     .WithPixels(rgba, image.Width, image.Height, p));
            Assert.True(Zlib.Compress(fresh.Bytes).Length <= budget, fresh.Report);
        }
    }

    [RomFact]
    public void AnUnchangedScreenIsLeftAlone()
    {
        using var session = new RomSession(TestRom.Path!);
        var (entry, image) = session.MenuImages.First(m => m.Entry.Index == 5320);
        int p = image.DisplayPalette;

        var fit = AssetIo.FitMenuToOriginal(session, entry.Index,
            c => image.WithPixels(image.ToRgba(p), image.Width, image.Height, p));

        Assert.Equal(AssetIo.FullColour, fit.Colours);
        Assert.StartsWith("identical", fit.Report);
    }
}
