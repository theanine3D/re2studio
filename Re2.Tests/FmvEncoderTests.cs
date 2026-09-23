using System;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Video;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Converting a modern video into a stream the game will play.</summary>
public sealed class FmvEncoderTests
{
    private readonly ITestOutputHelper _out;
    public FmvEncoderTests(ITestOutputHelper o) => _out = o;

    private static bool Skip => !Ffmpeg.Available;

    /// <summary>The cart's own streams must pass the check an import is held to.</summary>
    [RomFact]
    public void TheGamesOwnMoviesSatisfyTheImportRules()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        foreach (var movie in FmvIndex.Build(rom, directory).Take(40))
        {
            var entry = directory.Entries.First(e => e.Index == movie.AssetId);
            Assert.True(directory.TryGetData(rom, entry, out var data));
            Assert.Null(FmvEncoder.Reject(data));
        
}
    }

    [Fact]
    public void SomethingThatIsNotAStreamIsRejectedWithAReason()
    {
        var reason = FmvEncoder.Reject(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.NotNull(reason);
        Assert.Contains("sequence header", reason);
    }

    /// <summary>
    /// A converted stream carries no name of its own, so the importer puts the slot's name back.
    /// </summary>
    [RomFact]
    public void NamingAStreamPutsItWhereTheGameKeepsIt()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var movie = FmvIndex.Build(rom, directory).First();

        var entry = directory.Entries.First(e => e.Index == movie.AssetId);
        Assert.True(directory.TryGetData(rom, entry, out var original));

        // Strip the name the way a re-encode would, then put it back.
        int headerEnd = Mpeg1Sequence.HeaderLength(original);
        int bodyAt = Mpeg1Sequence.Find(original, Mpeg1Sequence.GroupStart);

        var nameless = original[..headerEnd].Concat(original[bodyAt..]).ToArray();
        Assert.Equal("", FmvIndex.ReadName(nameless));

        var named = FmvEncoder.WithName(nameless, movie.Name);

        _out.WriteLine($"{movie.Name}: {nameless.Length:N0} -> {named.Length:N0} bytes " +
                       $"(+{FmvEncoder.NameBlockLength(movie.Name)} expected)");

        Assert.Equal(movie.Name, FmvIndex.ReadName(named));
        Assert.Equal(nameless.Length + FmvEncoder.NameBlockLength(movie.Name), named.Length);

        // The header is untouched and the pictures all survive.
        Assert.Equal(Mpeg1Sequence.Read(original), Mpeg1Sequence.Read(named));
        Assert.Equal(movie.Pictures, Mpeg1Sequence.Count(named, Mpeg1Sequence.PictureStart));

        // Naming twice replaces rather than stacks.
        var again = FmvEncoder.WithName(named, movie.Name);
        Assert.Equal(named.Length, again.Length);
        Assert.Equal(movie.Name, FmvIndex.ReadName(again));

        // And the retail stream itself round-trips to the same bytes.
        Assert.Equal(original, FmvEncoder.WithName(original, movie.Name));
    }

    /// <summary>The player must produce exactly the frames the stream holds.</summary>
    [RomFact]
    public void DecodingProducesExactlyTheFramesTheStreamHolds()
    {
        if (Skip) { _out.WriteLine("ffmpeg not installed; skipped"); return; }

        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        foreach (var movie in FmvIndex.Build(rom, directory).Where(m => m.Pictures < 60).Take(5))
        {
            var entry = directory.Entries.First(e => e.Index == movie.AssetId);
            Assert.True(directory.TryGetData(rom, entry, out var data));

            var decoded = FmvFrames.Decode(data, out string error);

            _out.WriteLine($"{movie.Name}: {movie.Pictures} in the stream, " +
                           $"{decoded.Frames.Count} decoded, {decoded.Width}x{decoded.Height}");

            Assert.Equal("", error);
            Assert.Equal(movie.Pictures, decoded.Frames.Count);
            Assert.Equal(movie.Width, decoded.Width);
            Assert.Equal(movie.Height, decoded.Height);
            Assert.All(decoded.Frames, f => Assert.Equal(movie.Width * movie.Height * 4, f.Length));
        }
    }

    /// <summary>
    /// A real conversion: take a movie out of the ROM, wrap it as MP4 the way a video editor would
    /// export one, convert it back, and check the result is something the game would accept and
    /// that it fits the slot it came from.
    /// </summary>
    [RomFact]
    public void ConvertingAnMp4ProducesAConformingStreamThatFits()
    {
        if (Skip) { _out.WriteLine("ffmpeg not installed; skipped"); return; }

        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var movie = FmvIndex.Build(rom, directory).First(m => m.StoredSize is > 40_000 and < 200_000);

        var entry = directory.Entries.First(e => e.Index == movie.AssetId);
        Assert.True(directory.TryGetData(rom, entry, out var original));

        string scratch = Path.Combine(Path.GetTempPath(), "re2-fmv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            string source = Path.Combine(scratch, "source.m2v");
            string mp4 = Path.Combine(scratch, "source.mp4");
            File.WriteAllBytes(source, original);

            Assert.True(Ffmpeg.Run(new[] { "-y", "-v", "error", "-i", source,
                                           "-c:v", "libx264", "-pix_fmt", "yuv420p", mp4 },
                                   out string made), made);

            var result = FmvEncoder.EncodeToFit(mp4, movie.StoredSize, scratch);

            _out.WriteLine($"{movie.Name}: slot {movie.StoredSize:N0} bytes");
            _out.WriteLine($"  search: {result.Log}");
            _out.WriteLine($"  chose {result.Kilobits} kbit/s -> {result.Data.Length:N0} bytes, fits {result.Fits}");

            Assert.True(result.Fits, "nothing in the search fitted the slot");
            Assert.True(result.Data.Length <= movie.StoredSize);

            // Conforming before the header is fixed up, except for the rate fields.
            Assert.Null(FmvEncoder.Reject(result.Data));

            var before = Mpeg1Sequence.Read(result.Data);
            _out.WriteLine($"  as encoded: bit rate code {before.BitRateCode}, vbv {before.VbvBufferSize}");

            var conformed = result.Data.ToArray();
            FmvEncoder.Conform(conformed);

            var after = Mpeg1Sequence.Read(conformed);
            _out.WriteLine($"  conformed : bit rate code {after.BitRateCode}, vbv {after.VbvBufferSize}");

            Assert.Equal(Mpeg1Sequence.RetailVbvBufferSize, after.VbvBufferSize);
            Assert.NotEqual(Mpeg1Sequence.VariableBitRate, after.BitRateCode);
            Assert.Equal(before.Width, after.Width);
            Assert.Equal(before.Height, after.Height);
            Assert.Equal(before.FrameRateCode, after.FrameRateCode);

            // And it still decodes: the parser reads it back as a movie of about the right length.
            var parsed = FmvIndex.Parse(movie.AssetId, conformed);
            Assert.NotNull(parsed);
            _out.WriteLine($"  reparsed  : {parsed!.Pictures} pictures, {parsed.Seconds:0.00}s " +
                           $"(original {movie.Pictures} pictures, {movie.Seconds:0.00}s)");

            Assert.True(parsed.Pictures > movie.Pictures * 0.8,
                        $"lost too many frames: {parsed.Pictures} from {movie.Pictures}");
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }
    /// <summary>
    /// A source longer than the slot is cut to length before the bit rate is chosen, not squeezed in
    /// whole.
    /// </summary>
    [RomFact]
    public void AnOverlongSourceIsCutToTheSlotBeforeQualityIsTraded()
    {
        if (Skip) { _out.WriteLine("ffmpeg not installed; skipped"); return; }

        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        // A short slot, and a source several times its length.
        var slot = FmvIndex.Build(rom, directory).First(m => m.Pictures is > 20 and < 40);
        var longer = FmvIndex.Build(rom, directory).First(m => m.Pictures > 300);

        var entry = directory.Entries.First(e => e.Index == longer.AssetId);
        Assert.True(directory.TryGetData(rom, entry, out var source));

        string scratch = Path.Combine(Path.GetTempPath(), "re2-cut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            string mp4 = Path.Combine(scratch, "long.mp4");
            string raw = Path.Combine(scratch, "long.m2v");
            File.WriteAllBytes(raw, source);

            Assert.True(Ffmpeg.Run(new[] { "-y", "-v", "error", "-i", raw,
                                           "-c:v", "libx264", "-pix_fmt", "yuv420p", mp4 },
                                   out string made), made);

            var result = FmvEncoder.EncodeToFit(mp4, slot.StoredSize, scratch, slot.Seconds);
            var parsed = FmvIndex.Parse(slot.AssetId, result.Data);

            _out.WriteLine($"slot {slot.Name}: {slot.Pictures} frames, {slot.Seconds:0.00}s, " +
                           $"{slot.StoredSize:N0} bytes");
            _out.WriteLine($"source {longer.Name}: {longer.Pictures} frames, {longer.Seconds:0.00}s");
            _out.WriteLine($"result: {parsed!.Pictures} frames, {parsed.Seconds:0.00}s, " +
                           $"{result.Data.Length:N0} bytes at {result.Kilobits} kbit/s");

            Assert.True(result.Fits);

            // Cut to the slot's length, give or take the frame the encoder rounds to.
            Assert.True(parsed.Pictures <= slot.Pictures + 1,
                        $"{parsed.Pictures} frames for a {slot.Pictures} frame slot");

            // And because it was cut first, the budget bought real quality rather than the floor.
            Assert.True(result.Kilobits > LowestReasonable,
                        $"only reached {result.Kilobits} kbit/s, which means the budget went on " +
                        "footage that was going to be thrown away");
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Well above the search floor: anything near it means the fit was fighting length.</summary>
    private const int LowestReasonable = 120;

    /// <summary>Cutting an .m2v keeps whole groups and closes the stream properly.</summary>
    [RomFact]
    public void TruncatingAStreamCutsAtAGroupAndClosesIt()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var movie = FmvIndex.Build(rom, directory).First(m => m.Gops > 6);

        var entry = directory.Entries.First(e => e.Index == movie.AssetId);
        Assert.True(directory.TryGetData(rom, entry, out var data));

        int want = movie.Pictures / 2;
        var cut = FmvEncoder.TruncateToPictures(data, want, out int kept);

        _out.WriteLine($"{movie.Name}: {movie.Pictures} frames in {movie.Gops} groups -> " +
                       $"asked for {want}, kept {kept} ({cut.Length:N0} of {data.Length:N0} bytes)");

        Assert.True(kept <= want, $"kept {kept} for a limit of {want}");
        Assert.True(kept > 0);
        Assert.Equal(kept, Mpeg1Sequence.Count(cut, Mpeg1Sequence.PictureStart));
        Assert.True(cut.Length < data.Length);

        // Ends the way every retail stream does.
        Assert.Equal(new byte[] { 0, 0, 1, Mpeg1Sequence.SequenceEnd }, cut[^4..]);

        // Still a valid movie, and asking for more than it holds leaves it alone.
        Assert.Null(FmvEncoder.Reject(cut));
        Assert.Equal(data.Length, FmvEncoder.TruncateToPictures(data, movie.Pictures, out _).Length);
    }

}
