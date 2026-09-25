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

/// <summary>Locates "Resident Evil 2 (Europe) (En,Fr)" beside the USA cart.</summary>
public static class EuropeRom
{
    private static readonly Lazy<string?> PathLazy = new(() =>
    {
        string? folder = TestRom.Path is null ? null : System.IO.Path.GetDirectoryName(TestRom.Path);
        if (folder is null) return null;
        foreach (string ext in new[] { ".n64", ".z64", ".v64" })
        {
            string path = System.IO.Path.Combine(folder, "Resident Evil 2 (Europe) (En,Fr)" + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    });

    public static string? Path => PathLazy.Value;

    private static readonly Lazy<RomFile?> RomLazy = new(() => Path is null ? null : RomFile.Load(Path));
    public static RomFile Rom => RomLazy.Value ?? throw new InvalidOperationException("Europe ROM not available.");
}

/// <summary>Marks a fact that needs both the Europe cart and USA Rev 1.</summary>
public sealed class EuropeFactAttribute : FactAttribute
{
    public EuropeFactAttribute()
    {
        if (EuropeRom.Path is null || BothRoms.Rev1 is null)
            Skip = "Needs \"Resident Evil 2 (Europe) (En,Fr)\" beside the USA (Rev 1) ROM.";
    }
}

/// <summary>
/// Europe renumbers its assets and moves its tables, but the content is the same game: every reader
/// must find the same things in it that it finds in Rev 1.
/// </summary>
public class EuropeTests
{
    private static RomFile Eu => EuropeRom.Rom;
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

    [EuropeFact]
    public void IsDetectedAsEurope()
    {
        Assert.Equal(Re2Release.Europe, Re2Version.Detect(Eu).Release);
        Assert.Equal(0x12CB0, OverlayTable.Locate(Eu.Data));
        Assert.Equal(0xC606C, EuDir.Value.AssetBaseRomOffset);
        Assert.Equal(8591, EuDir.Value.DeclaredFileCount);
    }

    [EuropeFact]
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

    [EuropeFact]
    public void ItemsMatchRev1()
    {
        var eu = ItemTable.Read(Eu);
        var us = ItemTable.Read(Us);
        Assert.Equal(us.Count, eu.Count);
        for (int i = 0; i < us.Count; i++)
            Assert.Equal(Hash(Us, UsDir.Value, us[i].MeshAssetId), Hash(Eu, EuDir.Value, eu[i].MeshAssetId));
    }

    [EuropeFact]
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

    [EuropeFact]
    public void SceneryPropsMatchRev1()
    {
        var eu = RoomPropTable.Read(Eu);
        var us = RoomPropTable.Read(Us);
        Assert.Equal(us.Count, eu.Count);
        Assert.Equal(us.Select(p => (p.Stage, p.Room, p.Slot)), eu.Select(p => (p.Stage, p.Room, p.Slot)));
    }

    [EuropeFact]
    public void EnglishInventoryTextMatchesRev1()
    {
        Assert.Equal(ItemNames.Read(Us), ItemNames.Read(Eu));
        Assert.Equal(ItemMessages.Read(Us), ItemMessages.Read(Eu));
    }

    [EuropeFact]
    public void FrenchInventoryTextReadsAndRoundTrips()
    {
        var overlay = ModelTextureTable.LoadMainOverlay(Eu);
        var names = ItemNames.Read(overlay, alternate: true);
        var messages = ItemMessages.Read(overlay, alternate: true);

        Assert.Equal("Couteau", names[1]);
        Assert.Contains(names, n => n.Contains("Amélioré"));
        Assert.Contains(messages, m => m.Contains("Mon inventaire est\nplein."));

        // Every name is plain text in the French glyph set, and writing it back reproduces it.
        // All but "Explosif <7D> déto.", whose 0x7D glyph has not been identified.
        Assert.Single(names, n => n.Contains('<'));
        Assert.True(ItemNames.TryWrite(overlay, names, out string error, alternate: true), error);
        Assert.True(ItemMessages.TryWrite(overlay, messages, out error, alternate: true), error);
        Assert.Equal(names, ItemNames.Read(overlay, alternate: true));
        Assert.Equal(messages, ItemMessages.Read(overlay, alternate: true));

        // And the English beside them is untouched.
        Assert.Equal(ItemNames.Read(Us), ItemNames.Read(overlay));
    }

    [EuropeFact]
    public void FrenchDocumentsKeepTheirAccents()
    {
        var text = TextTable.Read(Eu, EuDir.Value);
        Assert.True(text.Count > 400, $"only {text.Count} documents");
        var accented = text.First(t => t.Text.Contains("Grâce"));
        Assert.Equal(accented.Text, TextTable.Decode(TextTable.Encode(accented.Text, latin1: true), latin1: true));
    }

    [EuropeFact]
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

    [EuropeFact]
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

    [EuropeFact]
    public void UneditedRebuildIsByteIdentical()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "re2-eu-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProjectFolder.Extract(Eu, folder);
            var output = RomFile.Load(EuropeRom.Path!);
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
}
