using System;
using System.Linq;
using Re2.Core.Patch;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>
/// Patch creation, checked the only way that really counts: apply what was produced and see whether
/// it reconstructs the modified ROM byte for byte.
/// </summary>
public class BpsPatchTests
{
    private readonly ITestOutputHelper _out;

    public BpsPatchTests(ITestOutputHelper output) => _out = output;

    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void APatchReconstructsTheTargetExactly()
    {
        var source = Random(200_000, 1);
        var target = source.ToArray();

        // A few edits of different shapes, including one at the very end.
        for (int i = 0; i < 40; i++) target[i * 4111] ^= 0xFF;
        Random(500, 2).CopyTo(target, 120_000);
        target[^1] ^= 0x5A;

        var patch = BpsPatch.Create(source, target);

        Assert.Equal(target, BpsPatch.Apply(patch, source).Target);
        _out.WriteLine($"patch {patch.Length:N0} bytes for {target.Length:N0} bytes of ROM");
    }

    [Fact]
    public void AnIdenticalRomGivesATinyPatch()
    {
        var source = Random(1 << 20, 3);
        var patch = BpsPatch.Create(source, source);

        Assert.Equal(source, BpsPatch.Apply(patch, source).Target);

        // Nothing changed, so the patch is a header, one long SourceRead and the checksums.
        _out.WriteLine($"no-change patch: {patch.Length} bytes");
        Assert.True(patch.Length < 64, $"a no-change patch took {patch.Length} bytes");
    }

    /// <summary>The checksum is the point: a patch must refuse the wrong ROM rather than corrupt it.</summary>
    [Fact]
    public void APatchRefusesARomItWasNotMadeFrom()
    {
        var source = Random(50_000, 4);
        var target = source.ToArray();
        target[1234] ^= 0xFF;

        var patch = BpsPatch.Create(source, target);
        var different = Random(50_000, 5);

        var error = Assert.Throws<InvalidOperationException>(() => BpsPatch.Apply(patch, different));
        Assert.Contains("not for this ROM", error.Message);
    }

    [Fact]
    public void APatchRefusesARomOfTheWrongSize()
    {
        var source = Random(50_000, 6);
        var patch = BpsPatch.Create(source, source);

        var error = Assert.Throws<InvalidOperationException>(() => BpsPatch.Apply(patch, Random(40_000, 7)));
        Assert.Contains("50,000", error.Message);
    }

    [Fact]
    public void TheMetadataSurvivesTheRoundTrip()
    {
        var source = Random(1000, 8);
        var target = source.ToArray();
        target[10] ^= 1;

        var patch = BpsPatch.Create(source, target, "RE2 Studio");

        Assert.Equal(target, BpsPatch.Apply(patch, source).Target);
    }

    /// <summary>The real thing: a patch between the retail cart and a build made from it.</summary>
    [RomFact]
    public void APatchOfTheRealRomRoundTrips()
    {
        var source = TestRom.Rom.Data;

        // A build-shaped change: something early, and something past the 16 MB IPS ceiling.
        var target = source.ToArray();
        for (int i = 0; i < 2000; i++) target[0x300000 + i] ^= 0x33;
        for (int i = 0; i < 5000; i++) target[0x3C40F14 + i] ^= 0x77;

        var patch = BpsPatch.Create(source, target, "RE2 Studio");
        var applied = BpsPatch.Apply(patch, source).Target;

        Assert.Equal(target.Length, applied.Length);
        Assert.True(target.AsSpan().SequenceEqual(applied), "the patched ROM does not match the build");

        _out.WriteLine($"{patch.Length:N0}-byte patch for a {source.Length:N0}-byte ROM " +
                       $"with 7,000 changed bytes, the last at 0x{0x3C40F14 + 5000:X} " +
                       $"({(0x3C40F14 + 5000) / 1048576.0:0.0} MB, past the IPS ceiling)");
    }
}
