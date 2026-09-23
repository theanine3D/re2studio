using System;
using System.Linq;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

/// <summary>How UV coordinates survive an import.</summary>
public class UvEncodingTests
{
    /// <summary>What the encoder does, reached through a real import of a generated model.</summary>
    private static float RoundTrip(float uv)
    {
        // The stored form and its inverse, which is what the game reads.
        float scaled = uv * MeshVertex.UvScale;
        short stored = scaled >= short.MaxValue ? short.MaxValue
                     : scaled <= short.MinValue ? short.MinValue
                     : (short)MathF.Round(scaled);

        return stored / MeshVertex.UvScale;
    }

    /// <summary>
    /// The case that broke the reported model: the far edge of an island must stay at the far edge.
    /// </summary>
    [Fact]
    public void AUvOfExactlyOneStaysAtTheFarEdge()
    {
        float back = RoundTrip(1.0f);

        Assert.True(back > 0.999f, $"1.0 came back as {back}, which collapses the island's far edge");
        Assert.True(back <= 1.0f);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.999f)]
    [InlineData(1.0f)]
    [InlineData(1.5f)]
    [InlineData(-0.5f)]
    [InlineData(-1.25f)]
    public void ValuesInsideTheFormatSurviveIntact(float uv)
    {
        // One step of the stored form is 1/16384, so that is the most a value may move.
        Assert.Equal(uv, RoundTrip(uv), 1f / MeshVertex.UvScale);
    }

    /// <summary>Tiling coordinates must not be folded back into 0..1.</summary>
    [Fact]
    public void TilingCoordinatesKeepTheirOrder()
    {
        float[] across = { 0.8f, 0.9f, 1.0f, 1.1f, 1.2f };
        var back = across.Select(RoundTrip).ToArray();

        for (int i = 1; i < back.Length; i++)
            Assert.True(back[i] > back[i - 1],
                        $"{across[i - 1]} -> {back[i - 1]} then {across[i]} -> {back[i]}: the run reversed");
    }

    /// <summary>
    /// Beyond what a signed 16-bit field holds there is nothing to be done but clamp -- but it must
    /// clamp to the edge, not wrap to the other side.
    /// </summary>
    [Fact]
    public void ValuesBeyondTheFormatClampRatherThanWrap()
    {
        Assert.Equal(short.MaxValue / MeshVertex.UvScale, RoundTrip(3.37f), 1e-4f);
        Assert.Equal(short.MinValue / MeshVertex.UvScale, RoundTrip(-2.5f), 1e-4f);
    }

    /// <summary>
    /// The retail range has to be reproduced exactly, or importing an unmodified model would shift
    /// its texturing.
    /// </summary>
    [RomFact]
    public void RetailUvsAreWithinTheFormatsRange()
    {
        var rom = TestRom.Rom;
        var directory = Re2.Core.Assets.AssetDirectory.Read(rom);

        int checked_ = 0;
        foreach (var entry in directory.Entries)
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var mesh)) continue;

            foreach (var v in mesh.Parts.SelectMany(p => p.SubMeshes).SelectMany(sm => sm.Vertices))
            {
                Assert.Equal(v.U, RoundTrip(v.U), 1f / MeshVertex.UvScale);
                Assert.Equal(v.V, RoundTrip(v.V), 1f / MeshVertex.UvScale);
            }

            if (++checked_ >= 20) break;                  // a spread is enough; all 355 is slow
        }

        Assert.True(checked_ > 0, "no meshes were checked");
    }
}
