using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public sealed class SoundBankBuilderTests
{
    private static (SoundDirectory Bank, byte[] Table, byte[] Data) Load()
    {
        var rom = TestRom.Rom;
        var assets = AssetDirectory.Read(rom);
        var bank = SoundDirectory.Read(rom, assets);

        byte[] Get(int id)
        {
            var entry = assets.Entries.First(e => e.Index == id);
            Assert.True(assets.TryGetData(rom, entry, out var data));
            return data;
        }

        return (bank, Get(SoundDirectory.SampleDirectoryAssetId), Get(SoundDirectory.SampleDataAssetId));
    }

    /// <summary>Rebuilding with nothing replaced must reproduce both assets byte for byte.</summary>
    [RomFact]
    public void RebuildingWithNoReplacementsIsByteIdentical()
    {
        var (bank, table, data) = Load();
        var (newTable, newData) = SoundBankBuilder.Rebuild(bank, Array.Empty<SoundBankBuilder.Replacement>());

        Assert.Equal(table.Length, newTable.Length);
        Assert.Equal(table, newTable);
        Assert.Equal(data.Length, newData.Length);
        Assert.Equal(data, newData);
    }

    /// <summary>Replacing one sample relocates the rest and still reads back correctly.</summary>
    [RomFact]
    public void ReplacingASampleRelocatesTheBankAndStillDecodes()
    {
        var (bank, _, _) = Load();
        var victim = bank.Samples.First(s => s.DeclaredLength is > 4000 and < 20000);

        // Something clearly different and clearly longer: a 2 second sweep.
        int rate = 22050, count = rate * 2;
        var tone = Enumerable.Range(0, count)
            .Select(i => (short)(Math.Sin(i * (0.02 + 0.00001 * i)) * 24000))
            .ToArray();

        var (newTable, newData) = SoundBankBuilder.Rebuild(
            bank, new[] { new SoundBankBuilder.Replacement(victim.Index, tone, rate) });

        Assert.Equal(bank.Samples.Count * SoundDirectory.RecordSize + SoundDirectory.TerminatorSize, newTable.Length);
        Assert.True(newData.Length > 0);

        // Re-parse the rebuilt bank the same way the game would, and check the swap took.
        var rebuilt = SoundDirectory.ReadFrom(newTable, newData);
        var replaced = rebuilt.Samples.First(s => s.Index == victim.Index);

        // The audio is brought back to the rate this sample already had, so that is what the record
        // declares; the duration is unchanged, which is what the length now reflects.
        int expected = (int)((long)count * victim.SampleRate / rate);

        Assert.Equal(victim.SampleRate, replaced.SampleRate);
        Assert.Equal(expected, (int)replaced.DeclaredLength);
        Assert.Equal(expected, rebuilt.Decode(replaced).Length);
        Assert.Equal(2.0, replaced.Seconds, 1);

        // Every other sample must still decode to its own declared length at its new offset.
        foreach (var s in rebuilt.Samples.Where(s => s.Index != victim.Index).Take(200))
            Assert.Equal((int)s.DeclaredLength, rebuilt.Decode(s).Length);
    }

    /// <summary>A replacement shorter than the old loop end must not leave a loop past the data.</summary>
    [RomFact]
    public void ShorteningALoopingSampleClearsItsLoop()
    {
        var (bank, _, _) = Load();
        var looping = bank.Samples.First(s => s.IsLooping && s.LoopEnd > 3000);

        var shortPcm = new short[1000];
        var (table, data) = SoundBankBuilder.Rebuild(
            bank, new[] { new SoundBankBuilder.Replacement(looping.Index, shortPcm, 11025) });

        var rebuilt = SoundDirectory.ReadFrom(table, data);
        var s2 = rebuilt.Samples.First(s => s.Index == looping.Index);

        Assert.False(s2.IsLooping);
        Assert.Equal(1000, (int)s2.DeclaredLength);
    }

    /// <summary>Samples past the declared end, from the frames a sample actually stores.</summary>
    private static int Tail(SoundSample s)
        => SoundCodec.CapacityInSamples(s.StoredSize) - (int)s.DeclaredLength;

    /// <summary>
    /// Every retail sample stores 72-135 samples past its declared end, and the game needs them:
    /// replaced samples cut off at the next frame boundary silenced every voice line.
    /// </summary>
    [RomFact]
    public void ReplacedSamplesKeepRetailsTail()
    {
        var (bank, _, _) = Load();
        Assert.All(bank.Samples, s => Assert.InRange(Tail(s), SoundBankBuilder.TailSamples, SoundBankBuilder.TailSamples + 63));

        // One replacement for every length modulo a frame, so each rounding case is covered.
        var victims = bank.Samples.Where(s => s.SampleRate > 0).Take(SoundCodec.SamplesPerFrame).ToList();
        var replacements = victims.Select((s, k) => new SoundBankBuilder.Replacement(
            s.Index, Enumerable.Range(0, 1000 + k).Select(i => (short)(i % 200 * 100)).ToArray(), s.SampleRate)).ToList();

        var (table, data) = SoundBankBuilder.Rebuild(bank, replacements);
        var rebuilt = SoundDirectory.ReadFrom(table, data);

        Assert.All(rebuilt.Samples, s => Assert.InRange(Tail(s), SoundBankBuilder.TailSamples, SoundBankBuilder.TailSamples + 63));
        Assert.All(victims, v => Assert.Equal(1000 + victims.IndexOf(v),
                                              (int)rebuilt.Samples.First(s => s.Index == v.Index).DeclaredLength));
    }

    /// <summary>
    /// A bank written without tails is repaired by Normalise, with the padding decoding to silence
    /// and every other sample untouched; retail's own bank passes through unchanged.
    /// </summary>
    [RomFact]
    public void NormaliseRepairsAMissingTailAndLeavesRetailAlone()
    {
        var (_, table, data) = Load();

        var (sameTable, sameData) = SoundBankBuilder.Normalise(table, data);
        Assert.Equal(table, sameTable);
        Assert.Equal(data, sameData);

        // The last sample runs to the end of the blob, so cutting the blob short strips its tail
        // without touching the directory: the shape an unpadded import left behind.
        var last = SoundDirectory.ReadFrom(table, data).Samples[^1];
        int frames = ((int)last.DeclaredLength + SoundCodec.SamplesPerFrame - 1) / SoundCodec.SamplesPerFrame;
        var cut = data[..(last.Offset + SoundCodec.BookBytes + frames * SoundCodec.FrameBytes)];
        Assert.True(Tail(SoundDirectory.ReadFrom(table, cut).Samples[^1]) < SoundBankBuilder.TailSamples);

        var (fixedTable, fixedData) = SoundBankBuilder.Normalise(table, cut);
        var repaired = SoundDirectory.ReadFrom(fixedTable, fixedData);

        Assert.InRange(Tail(repaired.Samples[^1]), SoundBankBuilder.TailSamples, SoundBankBuilder.TailSamples + 63);
        Assert.Equal(data.AsSpan(0, last.Offset).ToArray(), fixedData.AsSpan(0, last.Offset).ToArray());

        var pcm = SoundCodec.Decode(repaired.GetEncoded(repaired.Samples[^1]), 0);
        Assert.All(pcm.Skip(frames * SoundCodec.SamplesPerFrame), x => Assert.Equal(0, x));
    }
}
