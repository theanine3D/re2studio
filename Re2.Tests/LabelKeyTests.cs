using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Re2.Core.Assets;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

/// <summary>
/// Labels live in one file keyed by Rev 1's numbering, so each release's translation to it must
/// land every asset on its own Rev 1 counterpart and never two assets on one key.
/// </summary>
public class LabelKeyTests
{
    private static readonly Lazy<RomFile> Rev1 = new(() => RomFile.Load(BothRoms.Rev1!));

    [EuropeFact]
    public void EuropeTranslatesOntoTheSameContent() => Check(EuropeRom.Rom, localisedBackgrounds: 1);

    [JapanFact]
    public void JapanTranslatesOntoTheSameContent() => Check(JapanRom.Rom, localisedBackgrounds: 2);

    [BothRomsFact]
    public void Rev0SharesRev1sNumbering()
    {
        var rev0 = RomFile.Load(BothRoms.Rev0!);
        Assert.Null(rev0.Layout.Rev1IdRuns);

        var a = Hashes(rev0, AssetDirectory.Read(rev0));
        var b = Hashes(Rev1.Value, AssetDirectory.Read(Rev1.Value));
        Assert.Equal(b.Keys.OrderBy(k => k), a.Keys.OrderBy(k => k));

        // The revision's own fixes; everything else is the same asset under the same number.
        Assert.True(a.Count(p => b[p.Key] != p.Value) <= 2);
    }

    private static void Check(RomFile rom, int localisedBackgrounds)
    {
        var layout = rom.Layout;
        var directory = AssetDirectory.Read(rom);
        var rev1Directory = AssetDirectory.Read(Rev1.Value);

        var mine = Hashes(rom, directory);
        var theirs = Hashes(Rev1.Value, rev1Directory);
        var kinds = rev1Directory.Entries.ToDictionary(e => e.Index, e => e.Kind);

        // One key per asset, and a translated asset is the same kind of thing as its counterpart.
        // Localised ones (French or Japanese text on a texture, say) may differ in bytes, but a
        // wrong run shows up as far more than a handful of them.
        var seen = new Dictionary<int, int>();
        int same = 0, mapped = 0;
        foreach (var entry in directory.Entries)
        {
            int rev1 = layout.ToRev1AssetId(entry.Index);
            if (rev1 < 0) continue;

            Assert.True(seen.TryAdd(rev1, entry.Index),
                $"assets {seen.GetValueOrDefault(rev1)} and {entry.Index} both translate to Rev 1's {rev1}");
            Assert.True(kinds.TryGetValue(rev1, out var kind), $"{entry.Index} translates to {rev1}, which Rev 1 lacks");
            Assert.Equal(kind, entry.Kind);

            mapped++;
            if (mine.TryGetValue(entry.Index, out var h) && theirs.TryGetValue(rev1, out var g) && h == g) same++;
        }
        Assert.True(same >= mapped * 98 / 100, $"only {same} of {mapped} translated assets match Rev 1's content");

        var backgrounds = BackgroundIndex.BuildFromDirectory(rom, directory);
        var rev1Backgrounds = BackgroundIndex.BuildFromDirectory(Rev1.Value, rev1Directory);
        int differing = 0;
        foreach (var bg in backgrounds.Backgrounds)
        {
            int rev1 = layout.ToRev1Background(bg.Index);
            if (rev1 < 0) continue;
            if (!backgrounds.GetJpegBytes(rom, bg.Index).SequenceEqual(rev1Backgrounds.GetJpegBytes(Rev1.Value, rev1)))
                differing++;
        }
        Assert.Equal(localisedBackgrounds, differing);

        var clips = VoiceHashes(rom, directory);
        var rev1Clips = VoiceHashes(Rev1.Value, rev1Directory);
        for (int i = 0; i < clips.Count; i++)
        {
            int rev1 = layout.ToRev1VoiceClip(i);
            if (rev1 >= 0) Assert.Equal(rev1Clips[rev1], clips[i]);
        }
    }

    private static Dictionary<int, string> Hashes(RomFile rom, AssetDirectory directory)
    {
        var result = new Dictionary<int, string>();
        foreach (var e in directory.Entries)
            if (directory.TryGetData(rom, e, out var data)) result[e.Index] = Convert.ToHexString(SHA256.HashData(data));
        return result;
    }

    private static List<string> VoiceHashes(RomFile rom, AssetDirectory directory)
    {
        var entry = directory.Entries.First(e => e.Index == rom.Layout.VoiceBankAsset);
        Assert.True(directory.TryGetData(rom, entry, out var bank));
        return VoiceBank.Read(bank)
            .Select(c => Convert.ToHexString(SHA256.HashData(bank.AsSpan(c.Offset, c.StoredSize))))
            .ToList();
    }
}
