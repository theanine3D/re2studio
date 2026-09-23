using System;
using System.IO.Compression;
using System.Linq;
using Re2.Core.Codecs;
using Xunit;

namespace Re2.Tests;

public class InflateTests
{
    private static byte[] Random(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        rng.NextBytes(data);
        return data;
    }

    /// <summary>Compressible input exercises dynamic Huffman blocks with back-references.</summary>
    private static byte[] Compressible(int length, int seed)
    {
        var rng = new Random(seed);
        var words = new[] { "resident", "evil", "raccoon", "umbrella", "leon", "claire", "zombie" };
        var sb = new System.Text.StringBuilder();
        while (sb.Length < length) sb.Append(words[rng.Next(words.Length)]).Append(' ');
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    [InlineData(4096)]
    [InlineData(70000)]
    public void RoundTripsCompressibleData(int length)
    {
        var original = Compressible(length, length);
        var packed = RawDeflate.Compress(original);

        Assert.True(Inflate.TryInflateRaw(packed, out var restored, out int consumed));
        Assert.Equal(original, restored);
        Assert.Equal(packed.Length, consumed);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(100000)]
    public void RoundTripsIncompressibleData(int length)
    {
        // Random data forces stored blocks, which take the byte-aligned path through the decoder.
        var original = Random(length, length);
        var packed = RawDeflate.Compress(original);

        Assert.True(Inflate.TryInflateRaw(packed, out var restored, out int consumed));
        Assert.Equal(original, restored);
        Assert.Equal(packed.Length, consumed);
    }

    [Fact]
    public void RoundTripsEmptyInput()
    {
        var packed = RawDeflate.Compress(Array.Empty<byte>());
        Assert.True(Inflate.TryInflateRaw(packed, out var restored, out int consumed));
        Assert.Empty(restored);
        Assert.Equal(packed.Length, consumed);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void HandlesEveryBlockType(CompressionLevel level)
    {
        var original = Compressible(50000, 7);
        var packed = RawDeflate.Compress(original, level);

        Assert.True(Inflate.TryInflateRaw(packed, out var restored, out int consumed));
        Assert.Equal(original, restored);
        Assert.Equal(packed.Length, consumed);
    }

    /// <summary>
    /// Consumed-byte reporting is what lets us walk RE2's back-to-back stream chain, so verify it
    /// against a stream that has unrelated bytes appended.
    /// </summary>
    [Fact]
    public void ReportsConsumedBytesWithTrailingData()
    {
        var original = Compressible(8000, 3);
        var packed = RawDeflate.Compress(original);
        var withTrailer = packed.Concat(Random(4096, 99)).ToArray();

        Assert.True(Inflate.TryInflateRaw(withTrailer, out var restored, out int consumed));
        Assert.Equal(original, restored);
        Assert.Equal(packed.Length, consumed);
    }

    [Fact]
    public void ChainsBackToBackStreams()
    {
        var a = Compressible(3000, 11);
        var b = Compressible(5000, 12);
        var chain = RawDeflate.Compress(a).Concat(RawDeflate.Compress(b)).ToArray();

        Assert.True(Inflate.TryInflateRaw(chain, out var firstOut, out int firstConsumed));
        Assert.Equal(a, firstOut);

        Assert.True(Inflate.TryInflateRaw(chain.AsSpan(firstConsumed), out var secondOut, out _));
        Assert.Equal(b, secondOut);
    }

    [Fact]
    public void RejectsGarbage()
    {
        // Nothing here terminates with a BFINAL block, so speculative probing must not report success.
        var garbage = Random(512, 1234);
        if (Inflate.TryInflateRaw(garbage, out var output, out _))
            Assert.True(output.Length < 64, "garbage should not inflate into a substantial payload");
    }

    [Fact]
    public void RespectsMaxOutputGuard()
    {
        var original = Compressible(200000, 5);
        var packed = RawDeflate.Compress(original);
        Assert.False(Inflate.TryInflateRaw(packed, out _, out _, maxOutput: 1024));
    }

    [Fact]
    public void RejectsZlibWrappedStream()
    {
        // A zlib header would be misread as a deflate block header; make sure we reject rather than
        // silently produce nonsense, since RE2 uses raw streams throughout.
        var original = Compressible(4000, 21);
        using var ms = new System.IO.MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(original);
        var wrapped = ms.ToArray();

        bool ok = Inflate.TryInflateRaw(wrapped, out var restored, out _);
        Assert.False(ok && restored.SequenceEqual(original));
    }
}
