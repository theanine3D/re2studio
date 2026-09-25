using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Project;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

/// <summary>Locates "Biohazard 2 (Japan)" beside the USA cart.</summary>
public static class JapanRom
{
    private static readonly Lazy<string?> PathLazy = new(() =>
    {
        string? folder = TestRom.Path is null ? null : System.IO.Path.GetDirectoryName(TestRom.Path);
        if (folder is null) return null;
        foreach (string ext in new[] { ".n64", ".z64", ".v64" })
        {
            string path = System.IO.Path.Combine(folder, "Biohazard 2 (Japan)" + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    });

    public static string? Path => PathLazy.Value;

    private static readonly Lazy<RomFile?> RomLazy = new(() => Path is null ? null : RomFile.Load(Path));
    public static RomFile Rom => RomLazy.Value ?? throw new InvalidOperationException("Japan ROM not available.");
}

/// <summary>Marks a fact that needs both the Japan cart and USA Rev 1.</summary>
public sealed class JapanFactAttribute : FactAttribute
{
    public JapanFactAttribute()
    {
        if (JapanRom.Path is null || BothRoms.Rev1 is null)
            Skip = "Needs \"Biohazard 2 (Japan)\" beside the USA (Rev 1) ROM.";
    }
}

/// <summary>
/// Japan renumbers its assets and moves its tables, but the content is the same game: every reader
/// must find the same things in it that it finds in Rev 1.
/// </summary>
public class JapanTests
{
    private static RomFile Eu => JapanRom.Rom;
    private static readonly Lazy<RomFile> UsLazy = new(() => RomFile.Load(BothRoms.Rev1!));
    private static RomFile Us => UsLazy.Value;

    private static readonly Lazy<AssetDirectory> EuDir = new(() => AssetDirectory.Read(Eu));
    private static readonly Lazy<AssetDirectory> UsDir = new(() => AssetDirectory.Read(Us));

    private static string Hash(RomFile rom, AssetDirectory dir, int id)
    {
        var entry = dir.Entries.First(e => e.Index == id);
        Assert.True(dir.TryGetData(rom, entry, out var data), $"asset {id} would not decode");
        return Convert.ToHexString(SHA256.HashData(data));
    }

    [JapanFact]
    public void IsDetectedAsJapan()
    {
        Assert.Equal(Re2Release.Japan, Re2Version.Detect(Eu).Release);
        Assert.Equal(0x12D40, OverlayTable.Locate(Eu.Data));
        Assert.Equal(0xC5616, EuDir.Value.AssetBaseRomOffset);
        Assert.Equal(8177, EuDir.Value.DeclaredFileCount);
    }

    [JapanFact]
    public void CharactersMatchRev1()
    {
        for (int table = 0; table < ModelTextureTable.EntityTableAddresses.Length; table++)
        {
            var eu = ModelTextureTable.ReadCharacters(Eu, table);
            var us = ModelTextureTable.ReadCharacters(Us, table);
            Assert.True(us.Count == eu.Count,
                $"table {table}: us [{string.Join(",", us.Select(c => c.Index + ":" + c.MeshAssetId))}] " +
                $"eu [{string.Join(",", eu.Select(c => c.Index + ":" + c.MeshAssetId))}]");

            for (int i = 0; i < us.Count; i++)
            {
                Assert.Equal(us[i].Index, eu[i].Index);
                Assert.Equal(Hash(Us, UsDir.Value, us[i].MeshAssetId), Hash(Eu, EuDir.Value, eu[i].MeshAssetId));
                Assert.Equal(us[i].Textures.Count, eu[i].Textures.Count);
                Assert.Equal(us[i].AnimationAssetIds.Count, eu[i].AnimationAssetIds.Count);
            }
        }
    }

    [JapanFact]
    public void ItemsMatchRev1()
    {
        var eu = ItemTable.Read(Eu);
        var us = ItemTable.Read(Us);
        Assert.Equal(us.Count, eu.Count);
        for (int i = 0; i < us.Count; i++)
            Assert.Equal(Hash(Us, UsDir.Value, us[i].MeshAssetId), Hash(Eu, EuDir.Value, eu[i].MeshAssetId));
    }

    [JapanFact]
    public void RoomsMasksAndDoorsMatchRev1()
    {
        var euRooms = RoomTable.Read(Eu);
        var usRooms = RoomTable.Read(Us);
        Assert.Equal(usRooms.Select(r => r.ViewCount), euRooms.Select(r => r.ViewCount));

        var euMasks = MaskTable.Read(Eu);
        var usMasks = MaskTable.Read(Us);
        Assert.Equal(usMasks.Select(m => m.Length), euMasks.Select(m => m.Length));
        Assert.Equal(usMasks.Select(m => m.Count(id => id >= 0)), euMasks.Select(m => m.Count(id => id >= 0)));

        var euDoors = DoorTable.Read(Eu);
        var usDoors = DoorTable.Read(Us);
        Assert.Equal(usDoors.Count, euDoors.Count);
        Assert.Equal(usDoors.Select(d => (d.FromRoom, d.DestStage, d.DestRoom, d.DestX, d.DestZ)),
                     euDoors.Select(d => (d.FromRoom, d.DestStage, d.DestRoom, d.DestX, d.DestZ)));
    }

    [JapanFact]
    public void SceneryPropsMatchRev1()
    {
        var eu = RoomPropTable.Read(Eu);
        var us = RoomPropTable.Read(Us);
        Assert.Equal(us.Count, eu.Count);
        Assert.Equal(us.Select(p => (p.Stage, p.Room, p.Slot)), eu.Select(p => (p.Stage, p.Room, p.Slot)));
    }

    [JapanFact]
    public void EnglishInventoryTextMatchesRev1()
    {
        Assert.Equal(ItemNames.Read(Us), ItemNames.Read(Eu));
        Assert.Equal(ItemMessages.Read(Us), ItemMessages.Read(Eu));
    }

    [JapanFact]
    public void SoundsAndVoicesRead()
    {
        Assert.Equal(1192, SoundDirectory.Read(Eu, EuDir.Value).Samples.Count);

        var clips = VoiceBank.Read(Eu, EuDir.Value);
        Assert.True(clips.Count >= 588, $"only {clips.Count} voice clips");

        var directory = EuDir.Value;
        var entry = directory.Entries.First(e => e.Index == Eu.Layout.VoiceBankAsset);
        Assert.True(directory.TryGetData(Eu, entry, out var bank));
        var clip = clips[0];
        var pcm = MortCodec.Decode(Eu, bank.AsSpan(clip.Offset), clip.BlockCount);
        Assert.Contains(pcm, s => s != 0);
    }

    [JapanFact]
    public void IconsAndTheirPaletteAreWhereTheLayoutSays()
    {
        var directory = EuDir.Value;
        byte[] Asset(int id)
        {
            directory.TryGetData(Eu, directory.Entries.First(e => e.Index == id), out var d);
            return d;
        }

        Assert.True(MenuImage.TryParse(Asset(Eu.Layout.IconPaletteAsset), out _));
        foreach (var icon in InventoryIcons.For(Eu.Layout))
            Assert.True(InventoryIcons.Fits(icon, Asset(icon.AssetId)), $"icon {icon.Number}");
    }

    [JapanFact]
    public void UneditedRebuildIsByteIdentical()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "re2-jp-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProjectFolder.Extract(Eu, folder);
            var output = RomFile.Load(JapanRom.Path!);
            var result = ProjectFolder.Build(Eu, folder, output);
            Assert.Equal(0, result.BlobsRebuilt);
            Assert.True(result.ByteIdentical);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Eu.Data)), Convert.ToHexString(SHA256.HashData(output.Data)));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [JapanFact]
    public void JapaneseInventoryTextReadsAndRoundTrips()
    {
        var overlay = ModelTextureTable.LoadMainOverlay(Eu);
        var names = ItemNames.Read(overlay, alternate: true);
        var messages = ItemMessages.Read(overlay, alternate: true);

        Assert.Equal("コンバットナイフ", names[1]);
        Assert.Equal("火炎放射器", names[16]);
        Assert.Equal("秘書の日記A", names[111]);
        Assert.Contains(messages, m => m.StartsWith("ハーブを調合しますか?"));

        // Every name is in the Japanese glyph set, and writing them back reproduces them.
        Assert.DoesNotContain(names, n => n.Contains('<'));
        Assert.True(ItemNames.TryWrite(overlay, names, out string error, alternate: true), error);
        Assert.True(ItemMessages.TryWrite(overlay, messages, out error, alternate: true), error);
        Assert.Equal(names, ItemNames.Read(overlay, alternate: true));
        Assert.Equal(messages, ItemMessages.Read(overlay, alternate: true));
    }

    [Fact]
    public void JapaneseCharsetRoundTripsTypedText()
    {
        const string text = "ハンドガンの弾を取りました。へべ";
        Assert.True(ItemText.TryEncode(text, out var codes, out string error, ItemCharset.Japanese), error);
        Assert.Equal("ハンドガンの弾を取りました.ヘベ", ItemText.Decode(codes, ItemCharset.Japanese));
    }
}
