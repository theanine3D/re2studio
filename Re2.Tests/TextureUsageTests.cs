using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Codecs;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>
/// Finding textures no model draws any more, which is how a project that has outgrown the asset
/// region gets back under it.
/// </summary>
public class TextureUsageTests
{
    private readonly ITestOutputHelper _out;

    public TextureUsageTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// On the retail ROM almost everything listed is drawn -- the game does not carry much dead weight
    /// of its own.
    /// </summary>
    [RomFact]
    public void RetailDrawsNearlyEveryTextureItLists()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        byte[]? Mesh(int id)
        {
            var e = directory.Entries.FirstOrDefault(x => x.Index == id);
            return e is not null && directory.TryGetData(rom, e, out var d) ? d : null;
        }

        var report = TextureUsage.Analyse(rom, Mesh);

        _out.WriteLine($"{report.Listed.Count} listed, {report.Live.Count} live, {report.Dead.Count} dead");

        Assert.NotEmpty(report.Listed);
        Assert.Empty(report.Dead.Intersect(report.Live));

        // A mesh that will not parse must keep everything it lists, never free it.
        Assert.True(report.Dead.Count * 4 < report.Listed.Count,
                    $"{report.Dead.Count} of {report.Listed.Count} textures called dead on an unmodified ROM");
    }

    /// <summary>A blanked texture has to stay a texture.</summary>
    [RomFact]
    public void BlankingKeepsTheTextureReadableAndCostsAlmostNothing()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        int checkedCount = 0;
        long before = 0, after = 0;

        foreach (var entry in directory.Entries)
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (!TextureFile.TryParse(data, out var texture)) continue;
            if (texture!.Width * texture.Height < 256) continue;

            var blank = TextureUsage.Blank(texture);

            Assert.True(TextureFile.TryParse(blank, out var reloaded), $"asset {entry.Index} stopped being a texture");
            Assert.Equal(texture.Width, reloaded!.Width);
            Assert.Equal(texture.Height, reloaded.Height);

            before += Zlib.Compress(data).Length;
            after += Zlib.Compress(blank).Length;

            if (++checkedCount == 50) break;
        }

        _out.WriteLine($"{checkedCount} textures: {before:N0} -> {after:N0} bytes compressed");

        Assert.True(checkedCount > 0, "no textures found to blank");
        Assert.True(after * 4 < before, $"blanking saved too little: {before:N0} -> {after:N0}");
    }
}
