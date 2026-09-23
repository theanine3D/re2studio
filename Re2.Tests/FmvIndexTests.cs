using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>The movies in the ROM, and the profile every one of them declares.</summary>
public sealed class FmvIndexTests
{
    private readonly ITestOutputHelper _out;
    public FmvIndexTests(ITestOutputHelper o) => _out = o;

    [RomFact]
    public void EveryMovieIsTheSameMpegProfile()
    {
        var rom = TestRom.Rom;
        var movies = FmvIndex.Build(rom, AssetDirectory.Read(rom));

        _out.WriteLine($"{movies.Count} movies, {movies.Sum(m => (long)m.StoredSize):N0} bytes, " +
                       $"{movies.Sum(m => m.Pictures):N0} pictures, " +
                       $"{TimeSpan.FromSeconds(movies.Sum(m => m.Seconds)):mm\\:ss} total");

        _out.WriteLine($"sizes      {string.Join(", ", movies.Select(m => $"{m.Width}x{m.Height}").Distinct())}");
        _out.WriteLine($"frame rate {string.Join(", ", movies.Select(m => m.FrameRateCode).Distinct())}");
        _out.WriteLine($"aspect     {string.Join(", ", movies.Select(m => m.AspectRatioCode).Distinct())}");
        _out.WriteLine($"bit rate   {string.Join(", ", movies.Select(m => m.BitRateCode).Distinct().OrderBy(b => b))}");
        _out.WriteLine($"vbv        {string.Join(", ", movies.Select(m => m.VbvBufferSize).Distinct().OrderBy(v => v))}");
        _out.WriteLine($"matrices   intra {movies.Count(m => m.LoadIntraMatrix)}, " +
                       $"non-intra {movies.Count(m => m.LoadNonIntraMatrix)}, " +
                       $"constrained {movies.Count(m => m.ConstrainedParameters)}");

        Assert.True(movies.Count > 250, $"only found {movies.Count} movies");

        // The whole set is one profile, which is what makes a single import rule honest.
        Assert.All(movies, m => Assert.Equal(240, m.Width));
        Assert.All(movies, m => Assert.Equal(120, m.Height));
        Assert.All(movies, m => Assert.Equal(5, m.FrameRateCode));
        Assert.All(movies, m => Assert.Equal(Mpeg1Sequence.RetailVbvBufferSize, m.VbvBufferSize));
        Assert.All(movies, m => Assert.False(m.LoadIntraMatrix));
        Assert.All(movies, m => Assert.False(m.LoadNonIntraMatrix));

        // Every one carries the name it was authored under.
        Assert.All(movies, m => Assert.EndsWith(".m2v", m.Name, StringComparison.OrdinalIgnoreCase));
        _out.WriteLine("examples: " + string.Join(", ", movies.Take(6).Select(m => m.Name)));
    }

    /// <summary>Rewriting the rate fields must leave every other field untouched.</summary>
    [RomFact]
    public void SettingTheRateLeavesTheRestOfTheHeaderAlone()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var movie = FmvIndex.Build(rom, directory)[0];

        var entry = directory.Entries.First(e => e.Index == movie.AssetId);
        Assert.True(directory.TryGetData(rom, entry, out var data));

        var before = Mpeg1Sequence.Read(data);

        Mpeg1Sequence.SetRate(data, Mpeg1Sequence.VariableBitRate, 517);
        var middle = Mpeg1Sequence.Read(data);

        Assert.Equal(Mpeg1Sequence.VariableBitRate, middle.BitRateCode);
        Assert.Equal(517, middle.VbvBufferSize);

        Mpeg1Sequence.SetRate(data, before.BitRateCode, before.VbvBufferSize);
        Assert.Equal(before, Mpeg1Sequence.Read(data));
    }
}
