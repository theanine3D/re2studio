using System;
using System.Linq;
using Re2.Core.Codecs;
using Xunit;

namespace Re2.Tests;

public sealed class DeflateTests
{
    /// <summary>
    /// Inputs picked to reach every branch: empty and near-empty, shorter than a minimum match,
    /// incompressible, entirely one byte, and long enough to be split into several blocks.
    /// </summary>
    public static TheoryData<string, byte[]> Cases()
    {
        var data = new TheoryData<string, byte[]>();
        var random = new Random(20260922);

        data.Add("empty", Array.Empty<byte>());
        data.Add("one byte", new byte[] { 0x41 });
        data.Add("shorter than a match", new byte[] { 1, 2 });
        data.Add("all one byte", Enumerable.Repeat((byte)0xAA, 200_000).ToArray());

        var noise = new byte[100_000];
        random.NextBytes(noise);
        data.Add("incompressible", noise);

        var text = new byte[300_000];
        for (int i = 0; i < text.Length; i++) text[i] = (byte)("the quick brown fox "[i % 20]);
        data.Add("highly repetitive", text);

        // Two halves of different character, which is what block splitting exists for.
        var mixed = new byte[200_000];
        random.NextBytes(mixed.AsSpan(0, 100_000));
        for (int i = 100_000; i < mixed.Length; i++) mixed[i] = (byte)(i % 7);
        data.Add("two kinds of data", mixed);

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsWithinItsWindow(string name, byte[] data)
    {
        const int Window = Zlib.WindowSize;

        var packed = Deflate.Compress(data, Window);

        Assert.True(Inflate.TryInflateRaw(packed, out var back, out _, out int distance),
                    $"{name}: did not inflate");
        Assert.True(data.SequenceEqual(back), $"{name}: round-tripped to different bytes");
        Assert.True(distance <= Window, $"{name}: reached back {distance}, past the window");
    }

    /// <summary>
    /// The reason this encoder exists: everything over the window used to be cut into independent
    /// pieces, and one continuous stream has to beat that or there was no point.
    /// </summary>
    [Fact]
    public void BeatsCuttingTheInputIntoIndependentPieces()
    {
        var data = new byte[400_000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * i / 97);

        var ours = Deflate.Compress(data, Zlib.WindowSize);

        int pieces = 0;
        for (int at = 0; at < data.Length; at += Zlib.WindowSize)
            pieces += RawDeflate.Compress(
                data.AsSpan(at, Math.Min(Zlib.WindowSize, data.Length - at))).Length;

        Assert.True(ours.Length < pieces, $"ours {ours.Length}, in pieces {pieces}");
    }

    [Fact]
    public void ZlibUsesItForInputsTheBclCannotWindow()
    {
        // A distinctive run, a long compressible stretch, then that same run again.
        var random = new Random(7);
        var mark = new byte[20_000];
        random.NextBytes(mark);

        var data = new byte[mark.Length * 2 + 260_000];
        mark.CopyTo(data, 0);
        for (int i = 0; i < 260_000; i++) data[mark.Length + i] = (byte)(i % 1_000);
        mark.CopyTo(data, mark.Length + 260_000);

        var packed = Zlib.Compress(data);

        Assert.True(Zlib.TryDecompress(packed, out var back));
        Assert.True(data.SequenceEqual(back));
        Assert.True(Zlib.FitsWindow(packed.AsSpan(Zlib.HeaderSize)));

        // Stored blocks would be the giveaway that the fallback chain bottomed out.
        Assert.True(packed.Length < data.Length / 2, $"packed to {packed.Length} of {data.Length}");
    }

    /// <summary>
    /// The main overlay has to recompress into the space it already occupies, because the overlays are
    /// packed end to end with nothing spare between them.
    /// </summary>
    [RomFact]
    public void MainOverlayRecompressesIntoItsOwnSlot()
    {
        var rom = TestRom.Rom;
        var main = Re2.Core.Rom.OverlayTable.Read(rom.Data).Find(e => e.Index == 1);
        Assert.NotNull(main);

        Assert.True(Re2.Core.Rom.OverlayTable.TryDecompress(rom.Data, main!, out var raw));

        var ours = Deflate.Compress(raw, Zlib.WindowSize);
        int slot = main!.CompressedSize - Re2.Core.Rom.OverlayTable.BlockHeaderSize;

        Assert.True(Inflate.TryInflateRaw(ours, out var back, out _, out int distance));
        Assert.True(raw.SequenceEqual(back));
        Assert.True(distance <= Zlib.WindowSize);
        Assert.True(ours.Length <= slot, $"recompressed to {ours.Length:N0}, slot holds {slot:N0}");
    }
}
