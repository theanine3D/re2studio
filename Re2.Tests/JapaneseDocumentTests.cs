using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Emulation;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

/// <summary>Japan's documents: pictures of text, run-length coded (see JapaneseDocument).</summary>
public class JapaneseDocumentTests
{
    private static int[] DocumentIds()
    {
        var rom = JapanRom.Rom;
        var dir = AssetDirectory.Read(rom);
        return dir.Entries.Where(e => e.Index is >= 2127 and <= 2294).Select(e => e.Index).Distinct().ToArray();
    }

    private static byte[] Asset(int id)
    {
        var rom = JapanRom.Rom;
        var dir = AssetDirectory.Read(rom);
        dir.TryGetData(rom, dir.Entries.First(e => e.Index == id), out var data);
        return data;
    }

    /// <summary>The port against the game's own decoder, run in the interpreter.</summary>
    [JapanFact]
    public void DecodesExactlyAsTheGameDoes()
    {
        var rom = JapanRom.Rom;
        foreach (int id in new[] { 2127, 2140, 2200, 2294 })
        {
            var doc = Asset(id);
            Assert.True(JapaneseDocument.TryDecode(doc, out var page), $"asset {id}");

            var cpu = GameCode.Load(rom);
            const uint src = 0x80400000, dst = 0x80500000;
            cpu.Load(src, doc);
            cpu.Reg[4] = src;
            cpu.Reg[5] = dst;
            cpu.Reg[29] = 0x80600000;
            cpu.Call(0x80090F4C);

            Assert.Equal(page!.Width, cpu.Read16(dst));
            Assert.Equal(page.Height, cpu.Read16(dst + 2));
            for (int i = 0; i < page.Pixels.Length; i++)
            {
                byte texel = cpu.Read8(dst + 4 + (uint)(i / 2));
                int expected = i % 2 == 0 ? texel >> 4 : texel & 15;
                if (expected != page.Pixels[i]) Assert.Fail($"asset {id}: pixel {i} is {page.Pixels[i]}, the game says {expected}");
            }
        }
    }

    [JapanFact]
    public void EveryDocumentDecodesAndReEncodesToTheRetailBytes()
    {
        int identical = 0, total = 0;
        foreach (int id in DocumentIds())
        {
            var doc = Asset(id);
            Assert.True(JapaneseDocument.TryDecode(doc, out var page), $"asset {id} did not decode");
            total++;

            var encoded = page!.Encode();
            Assert.True(JapaneseDocument.TryDecode(encoded, out var again), $"asset {id}: re-encode unreadable");
            Assert.Equal(page.Pixels, again!.Pixels);

            if (encoded.AsSpan().SequenceEqual(doc.AsSpan(0, Math.Min(doc.Length, encoded.Length))) &&
                doc.Skip(encoded.Length).All(b => b == 0))
                identical++;
        }

        Assert.Equal(168, total);
        Assert.True(identical == total, $"{identical} of {total} re-encode byte for byte");
    }

    /// <summary>Export writes the four levels as greys; reading those greys back must give the page.</summary>
    [JapanFact]
    public void GreysRoundTrip()
    {
        Assert.True(JapaneseDocument.TryDecode(Asset(2128), out var page));
        var again = JapaneseDocument.FromGrey(page!.Width, page.Height, page.ToGrey());
        Assert.Equal(page.Pixels, again.Pixels);
    }

    /// <summary>An edited page goes through a project build and comes out of the new ROM as edited.</summary>
    [JapanFact]
    public void AnEditedPageSurvivesABuild()
    {
        var rom = JapanRom.Rom;
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "re2-jpdoc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = Re2.Core.Project.ProjectFolder.Extract(rom, folder);
            var blob = manifest.Blobs.First(b => b.Ids.Contains(2128));
            Assert.EndsWith("page2128.bin", blob.File);

            Assert.True(JapaneseDocument.TryDecode(Asset(2128), out var page));
            var pixels = page!.Pixels.ToArray();
            for (int x = 0; x < 64; x++) pixels[10 * page.Width + x] = 3;      // a white bar across row 10
            var edited = new JapaneseDocument(page.Width, page.Height, pixels);

            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, blob.File), edited.Encode());

            var output = Re2.Core.Rom.RomFile.Load(JapanRom.Path!);
            Re2.Core.Project.ProjectFolder.Build(rom, folder, output);

            var dir = AssetDirectory.Read(output);
            dir.TryGetData(output, dir.Entries.First(e => e.Index == 2128), out var built);
            Assert.True(JapaneseDocument.TryDecode(built, out var back));
            Assert.Equal(pixels, back!.Pixels);
        }
        finally
        {
            try { System.IO.Directory.Delete(folder, true); } catch (System.IO.IOException) { }
        }
    }
}
