using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

/// <summary>An imported WAV has to end up at a rate the console's audio driver is set up for.</summary>
public sealed class SoundRateTests
{
    [RomFact]
    public void AnImportedWavIsResampledToTheSamplesOwnRate()
    {
        var rom = TestRom.Rom;
        var bank = SoundDirectory.Read(rom, AssetDirectory.Read(rom));
        var sample = bank.Samples.First(s => s.SampleRate == 16000 && s.DeclaredLength > 25000);

        // Half a second of 44.1 kHz tone, as an editor would hand it over.
        var pcm = new short[22050];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Sin(i * 0.05) * 12000);

        var notes = new List<string>();
        var (table, _) = SoundBankBuilder.Rebuild(
            bank, new[] { new SoundBankBuilder.Replacement(sample.Index, pcm, 44100) }, notes.Add);

        var rebuilt = SoundDirectory.ReadFrom(table, bank.SampleData);
        var updated = rebuilt.Samples.First(s => s.Index == sample.Index);

        Assert.Equal(16000, updated.SampleRate);
        Assert.InRange(updated.DeclaredLength, 7800u, 8200u);           // 0.5s at 16 kHz
        Assert.Contains(notes, n => n.Contains("44,100") && n.Contains("16,000"));
        Assert.DoesNotContain(rebuilt.Samples, s => s.SampleRate > SoundBankBuilder.HighestRetailRate);
    }

    /// <summary>
    /// Importing into a sample that was already replaced must still aim at the game's rate.
    /// </summary>
    [RomFact]
    public void ASecondImportIsMeasuredAgainstTheCartNotThePreviousImport()
    {
        var rom = TestRom.Rom;
        var cart = SoundDirectory.Read(rom, AssetDirectory.Read(rom));
        var sample = cart.Samples.First(s => s.SampleRate == 16000 && s.DeclaredLength > 25000);

        var pcm = new short[22050];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Sin(i * 0.05) * 12000);

        // The state the bug needed: a project whose bank already declares 44.1 kHz for this sample,
        // which is what an import made before the fix leaves behind.
        var (edited, editedData) = SoundBankBuilder.Rebuild(cart, Array.Empty<SoundBankBuilder.Replacement>());
        int record = cart.Samples.ToList().FindIndex(s => s.Index == sample.Index) * SoundDirectory.RecordSize;
        BinaryPrimitives.WriteUInt32BigEndian(edited.AsSpan(record + 12),
            ((uint)SoundDirectory.RateWordMarker << 16) | 44100);

        var overridden = SoundDirectory.ReadFrom(edited, editedData);
        Assert.Equal(44100, overridden.Samples.First(s => s.Index == sample.Index).SampleRate);

        // Reading that bank as its own authority is the bug: the import matches the previous one.
        var withoutCart = SoundBankBuilder.Rebuild(
            overridden, new[] { new SoundBankBuilder.Replacement(sample.Index, pcm, 44100) });
        Assert.Equal(44100, SoundDirectory.ReadFrom(withoutCart.Directory, withoutCart.SampleData)
                                          .Samples.First(s => s.Index == sample.Index).SampleRate);

        // With the cart as the authority, the import lands on the game's own 16 kHz.
        var notes = new List<string>();
        var (table, data) = SoundBankBuilder.Rebuild(
            overridden, new[] { new SoundBankBuilder.Replacement(sample.Index, pcm, 44100) }, notes.Add, cart);

        var result = SoundDirectory.ReadFrom(table, data).Samples.First(s => s.Index == sample.Index);
        Assert.Equal(16000, result.SampleRate);
        Assert.Contains(notes, n => n.Contains("44,100") && n.Contains("16,000"));
    }

    [RomFact]
    public void ARateThatAlreadyMatchesIsLeftAlone()
    {
        var rom = TestRom.Rom;
        var bank = SoundDirectory.Read(rom, AssetDirectory.Read(rom));
        var sample = bank.Samples.First(s => s.SampleRate == 16000 && s.DeclaredLength > 25000);

        var pcm = new short[8000];
        var notes = new List<string>();
        SoundBankBuilder.Rebuild(bank, new[] { new SoundBankBuilder.Replacement(sample.Index, pcm, 16000) }, notes.Add);

        Assert.Empty(notes);
    }

    [RomFact]
    public void RebuildingWithNoReplacementsStillReproducesTheBank()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var bank = SoundDirectory.Read(rom, directory);

        var (table, data) = SoundBankBuilder.Rebuild(bank, Array.Empty<SoundBankBuilder.Replacement>());

        var entry = directory.Entries.First(e => e.Index == SoundDirectory.SampleDirectoryAssetId);
        Assert.True(directory.TryGetData(rom, entry, out var originalTable));

        Assert.Equal(originalTable, table);
        Assert.Equal(bank.SampleData, data);
    }

    [Fact]
    public void ResamplingHalvesLengthAndKeepsShape()
    {
        var pcm = new short[1000];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Sin(i * 0.02) * 10000);

        var down = SoundBankBuilder.Resample(pcm, 44100, 22050);
        Assert.Equal(500, down.Length);

        var up = SoundBankBuilder.Resample(down, 22050, 44100);
        Assert.Equal(1000, up.Length);

        // A round trip keeps the waveform recognisable rather than reversing or silencing it.
        Assert.True(up.Select((v, i) => Math.Abs(v - pcm[i])).Average() < 2000);
    }
}
