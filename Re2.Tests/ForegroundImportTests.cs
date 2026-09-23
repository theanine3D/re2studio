using System;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Studio;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Importing a foreground, through the path the button actually calls.</summary>
public sealed class ForegroundImportTests : IClassFixture<ProjectFixture>
{
    private readonly ITestOutputHelper _out;
    private readonly ProjectFixture _project;

    public ForegroundImportTests(ITestOutputHelper output, ProjectFixture project)
    {
        _out = output;
        _project = project;
    }

    [RomFact]
    public void ImportingAnEditedLayerRewritesThatCamerasMaskAndNothingElse()
    {
        var session = new RomSession(TestRom.Path!);

        // A background whose camera has a foreground made only of run-length pieces.
        int index = Enumerable.Range(0, 200)
            .First(i => session.MaskForBackground(i) is { } m && m.CoveredPixels > 0 &&
                        !m.Pieces.Any(p => p.HasOwnPixels));

        var mask = session.MaskForBackground(index)!;
        int assetId = session.MaskAssetForBackground(index);
        Assert.True(assetId > 0);

        // Export the layer the way the button does, then rub out its left half.
        var background = new byte[mask.Width * mask.Height * 4];
        Array.Fill(background, (byte)255);

        var layer = MaskRenderer.Compose(mask, background, mask.Width, mask.Height);

        int erased = 0;
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width / 2; x++)
            {
                int at = (y * mask.Width + x) * 4;
                if (layer[at + 3] != 0) erased++;
                layer[at + 3] = 0;
            }

        Assert.True(erased > 0, "the chosen mask has nothing in its left half to erase");

        string folder = _project.Folder;

        string png = Path.Combine(Path.GetTempPath(), "re2-fg-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        File.WriteAllBytes(png, ImageCodec.RgbaToPng(layer, mask.Width, mask.Height));

        string report = AssetIo.ImportForeground(session, folder, index, png);
        _out.WriteLine(report);

        // It must have written the mask's own asset, not the background's.
        Assert.Contains(assetId.ToString(), report);

        string file = Path.Combine(folder, report.Split("wrote ")[1].Split(" --")[0]
                                                 .Replace('/', Path.DirectorySeparatorChar));

        var bytes = File.ReadAllBytes(file);
        File.Delete(png);
        Assert.True(MaskFile.TryParse(bytes, out var reloaded), "the imported mask does not parse");

        // The shape lost its left half, and the geometry survived intact.
        Assert.Equal(mask.CoveredPixels - erased, reloaded!.CoveredPixels);
        Assert.Equal(mask.Pieces.Count, reloaded.Pieces.Count);

        for (int i = 0; i < mask.Pieces.Count; i++)
            Assert.Equal(mask.Pieces[i].Depth, reloaded.Pieces[i].Depth);

        _out.WriteLine($"bg{index:D4} asset {assetId}: {mask.CoveredPixels:N0} -> " +
                       $"{reloaded.CoveredPixels:N0} covered pixels, {bytes.Length:N0} bytes");
    }

    /// <summary>A camera with no foreground has nothing to replace, and says so rather than inventing one.</summary>
    [RomFact]
    public void ImportingWhereThereIsNoForegroundIsRefused()
    {
        var session = new RomSession(TestRom.Path!);

        int index = Enumerable.Range(0, 400).First(i => session.MaskForBackground(i) is null);

        var error = Assert.Throws<InvalidDataException>(
            () => AssetIo.ImportForeground(session, Path.GetTempPath(), index, "nowhere.png"));

        Assert.Contains("no foreground", error.Message);
    }
}
