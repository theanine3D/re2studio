using System;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Rom;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Re2.Tests;

public class BackgroundIndexTests
{
    /// <summary>
    /// Both routes -- the asset directory and a raw scan of the background region -- must agree
    /// exactly. They are independent, so agreement is real evidence the index is right.
    /// </summary>
    [RomFact]
    public void FindsEveryBackground()
    {
        var rom = TestRom.Rom;
        var viaDirectory = BackgroundIndex.Build(rom);
        var viaScan = BackgroundIndex.Build(rom, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);

        Assert.Equal(1227, viaDirectory.Count);
        Assert.Equal(1227, viaScan.Count);
        Assert.Equal(
            viaScan.Backgrounds.Select(b => (b.Offset, b.Length)).ToList(),
            viaDirectory.Backgrounds.Select(b => (b.Offset, b.Length)).ToList());
    }

    /// <summary>The contiguous block still starts and ends exactly where the ROM map says.</summary>
    [RomFact]
    public void ContiguousBlockMatchesTheRomMap()
    {
        var index = BackgroundIndex.Build(TestRom.Rom, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);
        Assert.Equal(Re2RomMap.BackgroundsStart, index[0].Offset);
        Assert.Equal(Re2RomMap.BackgroundsEnd, index[^1].Offset + index[^1].Length + Re2RomMap.InterFileGap);
    }

    [RomFact]
    public void EveryBackgroundIsBaselineFourTwoZero()
    {
        var index = BackgroundIndex.Build(TestRom.Rom);
        foreach (var bg in index.Backgrounds)
        {
            Assert.True(bg.Image.IsBaseline, $"background {bg.Index} is not baseline sequential");
            Assert.Equal("4:2:0", bg.Image.Subsampling);
            Assert.Equal(3, bg.Image.Components);
        }
    }

    [RomFact]
    public void DimensionsMatchTheKnownSet()
    {
        var index = BackgroundIndex.Build(TestRom.Rom);
        var histogram = index.DimensionHistogram().ToDictionary(x => x.Dimensions, x => x.Count);

        Assert.Equal(1222, histogram["320x224"]);
        Assert.Equal(5, histogram["320x240"]);
        Assert.Equal(2, histogram.Count);
    }

    /// <summary>The container's gap-and-align rule must hold across every adjacent pair.</summary>
    [RomFact]
    public void PackingRuleHoldsForEveryAdjacentPair()
    {
        var index = BackgroundIndex.Build(TestRom.Rom);
        Assert.Equal(index.Count - 1, index.CountAdjacentPairsMatchingPacking());
    }

    [RomFact]
    public void SlotCapacityIsNeverSmallerThanTheStoredImage()
    {
        var index = BackgroundIndex.Build(TestRom.Rom);
        foreach (var bg in index.Backgrounds)
            Assert.True(bg.SlotCapacity >= bg.Length, $"background {bg.Index} slot is smaller than its contents");
    }
}

public class AssetContainerTests
{
    /// <summary>
    /// The structural walk and the JPEG-marker scan are independent methods of finding the same
    /// files, so agreeing on all 1,227 offsets and sizes is strong evidence both are right.
    /// </summary>
    [RomFact]
    public void ContainerWalkAgreesWithTheJpegScan()
    {
        var rom = TestRom.Rom;
        var walked = AssetContainer.Walk(rom.Data, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);
        var scanned = BackgroundIndex.Build(rom, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);

        Assert.Equal(scanned.Count, walked.Count);
        for (int i = 0; i < walked.Count; i++)
        {
            Assert.Equal(scanned[i].Offset, walked[i].Offset);
            Assert.Equal(scanned[i].Length, walked[i].Size);
        }
    }

    [RomFact]
    public void BackgroundFilesAllShareOneTypeTag()
    {
        var walked = AssetContainer.Walk(TestRom.Rom.Data, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);
        Assert.All(walked, f => Assert.Equal(0x00010000u, f.Tag));
    }

    [RomFact]
    public void WalkingBackwardsReachesTheSameBoundaries()
    {
        var rom = TestRom.Rom;
        var forward = AssetContainer.Walk(rom.Data, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);

        // Step back from the last file and confirm we land on the previous one.
        for (int i = forward.Count - 1; i > forward.Count - 25; i--)
        {
            Assert.True(AssetContainer.TryReadPreviousFile(rom.Data, forward[i].Offset, out int prev, out int size, out _));
            Assert.Equal(forward[i - 1].Offset, prev);
            Assert.Equal(forward[i - 1].Size, size);
        }
    }
}

public class BackgroundCodecTests
{
    private static Image<Rgba32> MakeImage(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image[x, y] = new Rgba32((byte)(x * 3), (byte)(y * 5), (byte)((x ^ y) & 0xFF), 255);
        return image;
    }

    [Fact]
    public void EncodesBaselineJpegTheGameCanDecode()
    {
        using var image = MakeImage(320, 224);
        var jpeg = BackgroundCodec.EncodeBaseline(image, 85);

        Assert.True(BackgroundCodec.IsGameDecodable(jpeg, out string reason), reason);
        Assert.True(Jfif.TryParse(jpeg, 0, out var parsed, out _));
        Assert.True(parsed.IsBaseline);
        Assert.Equal("4:2:0", parsed.Subsampling);
        Assert.Equal(320, parsed.Width);
        Assert.Equal(224, parsed.Height);
    }

    [Fact]
    public void EncodeToFitDropsQualityUntilItFits()
    {
        using var image = MakeImage(320, 224);
        int big = BackgroundCodec.EncodeBaseline(image, 90).Length;

        var result = BackgroundCodec.EncodeToFit(image, big / 2);
        Assert.True(result.FitsSlot);
        Assert.True(result.Size <= big / 2);
        Assert.True(result.Quality < 90);
    }

    [Fact]
    public void EncodeToFitReportsFailureWhenNothingFits()
    {
        using var image = MakeImage(320, 224);
        var result = BackgroundCodec.EncodeToFit(image, 128);
        Assert.False(result.FitsSlot);
    }

    [Fact]
    public void EncodeClosestUnderStaysWithinBudget()
    {
        using var image = MakeImage(320, 224);
        int budget = BackgroundCodec.EncodeBaseline(image, 60).Length;

        var result = BackgroundCodec.EncodeClosestUnder(image, budget);
        Assert.True(result.FitsSlot);
        Assert.True(result.Size <= budget);
        Assert.True(result.Quality >= 60);
    }

    [RomFact]
    public void ReimportingAnExportedBackgroundKeepsItsOriginalBytes()
    {
        var rom = TestRom.Rom;
        var index = BackgroundIndex.Build(rom);
        string dir = Path.Combine(Path.GetTempPath(), "re2-bg-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // An ordinary room and one of the swapped-chroma screens.
            foreach (int i in new[] { 60, 1223 })
            {
                var original = index.GetJpegBytes(rom, i).ToArray();
                string png = Path.Combine(dir, $"bg{i:D4}.png");
                File.WriteAllBytes(png, BackgroundCodec.JpegToPng(original));

                var result = BackgroundCodec.EncodeForProject(png, index[i], original, index[i].Length);
                Assert.True(result.Unchanged);
                Assert.Equal(original, result.Data);

                // One pixel changed: re-encoded, but never larger than the original.
                using (var edited = Image.Load<Rgba32>(png))
                {
                    edited[0, 0] = new Rgba32((byte)(edited[0, 0].R ^ 0xFF), 0, 0, 255);
                    edited.SaveAsPng(png);
                }
                var reencoded = BackgroundCodec.EncodeForProject(png, index[i], original, index[i].Length);
                Assert.False(reencoded.Unchanged);
                Assert.True(reencoded.FitsSlot);
                Assert.True(reencoded.Size <= original.Length);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [RomFact]
    public void DecodesEveryRetailBackgroundWithoutError()
    {
        var rom = TestRom.Rom;
        var index = BackgroundIndex.Build(rom);

        // Sample rather than all 1,227 to keep the suite quick, but spread across the whole set.
        for (int i = 0; i < index.Count; i += 97)
        {
            var (pixels, w, h) = BackgroundCodec.JpegToRgba(index.GetJpegBytes(rom, i));
            Assert.Equal(index[i].Width, w);
            Assert.Equal(index[i].Height, h);
            Assert.Equal(w * h * 4, pixels.Length);
        }
    }

    /// <summary>The five ending screens are left exactly as the ROM stores them.</summary>
    [RomFact]
    public void TheEndingScreensAreDecodedLikeEveryOtherBackground()
    {
        var rom = TestRom.Rom;
        var index = BackgroundIndex.Build(rom);

        // The quirk itself is real: literally decoded, bg1223's faces carry no skin tone.
        double raw = SkinFraction(BackgroundCodec.JpegToRgba(index.GetJpegBytes(rom, 1223)).Pixels);
        Assert.True(raw < 0.01, $"bg1223 should decode cold, got {raw:P1} skin tones");

        // And it is left alone regardless, here as everywhere else.
        foreach (int i in new[] { 0, 60, 290, 600, 1221, 1222, 1223, 1224, 1225, 1226 })
        {
            var jpeg = index.GetJpegBytes(rom, i);
            using var direct = Image.Load<Rgba32>(jpeg);
            var expected = new byte[direct.Width * direct.Height * 4];
            direct.CopyPixelDataTo(expected);

            Assert.Equal(expected, BackgroundCodec.JpegToRgba(jpeg).Pixels);
        }
    }

    /// <summary>
    /// Importing one of the five puts back what it was given, with no colour transform on the way in.
    /// </summary>
    [RomFact]
    public void ReplacingAnEndingScreenKeepsItsColours()
    {
        var rom = TestRom.Rom;
        var index = BackgroundIndex.Build(rom);
        var target = index.Backgrounds.First(b => b.Index == 1222);

        // A strongly warm picture, whose colours an exchange would obviously wreck.
        using var source = new Image<Rgba32>(target.Width, target.Height);
        source.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++) row[x] = new Rgba32(220, 120, 40, 255);
            }
        });

        string path = Path.Combine(Path.GetTempPath(), $"re2-bg1222-{Guid.NewGuid():N}.png");
        try
        {
            source.SaveAsPng(path);

            var encoded = BackgroundCodec.EncodeForProject(
                path, target, index.GetJpegBytes(rom, 1222), target.Length);

            var (pixels, _, _) = BackgroundCodec.JpegToRgba(encoded.Data);

            // Warm in, warm out: red clearly above blue, as an exchange would invert.
            Assert.True(pixels[0] > 150, $"red channel came back at {pixels[0]}");
            Assert.True(pixels[2] < 100, $"blue channel came back at {pixels[2]} -- chroma exchanged");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Fraction of pixels inside a coarse skin-tone gamut, over RGBA bytes.</summary>
    private static double SkinFraction(byte[] rgba)
    {
        int hits = 0, total = rgba.Length / 4;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (r > 95 && g > 40 && b > 20 && max - min > 15 &&
                Math.Abs(r - g) > 15 && r > g && g > b) hits++;
        }
        return (double)hits / total;
    }
}

public class BackgroundWriterTests
{
    private static byte[] SampleJpeg()
    {
        using var image = new Image<Rgba32>(64, 64);
        return BackgroundCodec.EncodeBaseline(image, 80);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(64)]
    [InlineData(70000)]     // forces more than one comment segment
    public void PadsToExactSizeAndStaysDecodable(int deficit)
    {
        var jpeg = SampleJpeg();
        int target = jpeg.Length + deficit;

        var padded = BackgroundWriter.PadToExactSize(jpeg, target);

        Assert.Equal(target, padded.Length);
        Assert.True(BackgroundCodec.IsGameDecodable(padded, out string reason), reason);

        // Padding must not change the picture.
        var (before, w1, h1) = BackgroundCodec.JpegToRgba(jpeg);
        var (after, w2, h2) = BackgroundCodec.JpegToRgba(padded);
        Assert.Equal(w1, w2);
        Assert.Equal(h1, h2);
        Assert.Equal(before, after);
    }

    [Fact]
    public void PaddingKeepsTheSoiApp0Signature()
    {
        // The ROM scanner anchors on this prefix; padding must go after it, not before.
        var padded = BackgroundWriter.PadToExactSize(SampleJpeg(), SampleJpeg().Length + 500);
        Assert.Equal(0xFF, padded[0]);
        Assert.Equal(JpegMarker.Soi, padded[1]);
        Assert.Equal(0xFF, padded[2]);
        Assert.Equal(JpegMarker.App0, padded[3]);
    }

    [Fact]
    public void RefusesToPadWhenTheImageIsTooBig()
    {
        var jpeg = SampleJpeg();
        Assert.Throws<ArgumentException>(() => BackgroundWriter.PadToExactSize(jpeg, jpeg.Length - 1));
    }

    /// <summary>
    /// The whole point of padding is that the container is untouched: same file count, same
    /// offsets, same sizes, same trailers.
    /// </summary>
    [RomFact]
    public void ReplacingABackgroundLeavesTheContainerByteCompatible()
    {
        var rom = RomFile.Load(TestRom.Path!);
        var index = BackgroundIndex.Build(rom);
        var target = index.Backgrounds.First(b => b.Offset == Re2RomMap.BackgroundsStart);

        var before = AssetContainer.Walk(rom.Data, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);
        int backgroundsBefore = BackgroundIndex.Build(rom).Count;

        using var replacement = new Image<Rgba32>(target.Width, target.Height);
        var encoded = BackgroundCodec.EncodeToFit(replacement, target.SlotCapacity);
        Assert.True(encoded.FitsSlot);

        BackgroundWriter.Replace(rom, target, encoded.Data);

        Assert.True(BackgroundWriter.VerifyTrailerIntact(rom, target));

        var after = AssetContainer.Walk(rom.Data, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Offset, after[i].Offset);
            Assert.Equal(before[i].Size, after[i].Size);
            Assert.Equal(before[i].Tag, after[i].Tag);
        }

        // And the rescan still sees every background.
        Assert.Equal(backgroundsBefore, BackgroundIndex.Build(rom).Count);
    }

    [RomFact]
    public void ReplacementOnlyTouchesItsOwnSlot()
    {
        var original = RomFile.Load(TestRom.Path!);
        var rom = RomFile.Load(TestRom.Path!);
        var target = BackgroundIndex.Build(rom).Backgrounds.First(b => b.Offset == Re2RomMap.BackgroundsStart);

        using var replacement = new Image<Rgba32>(target.Width, target.Height);
        BackgroundWriter.Replace(rom, target, BackgroundCodec.EncodeToFit(replacement, target.SlotCapacity).Data);

        for (int i = 0; i < rom.Length; i++)
        {
            if (i >= target.Offset && i < target.Offset + target.Length) continue;
            if (original.Data[i] != rom.Data[i])
                Assert.Fail($"byte 0x{i:X} changed outside the target slot");
        }
    }
}
