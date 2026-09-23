using System.Linq;
using Re2.Core.Assets;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>The voice bank in asset 1979.</summary>
public class VoiceBankTests
{
    private readonly ITestOutputHelper _out;

    public VoiceBankTests(ITestOutputHelper output) => _out = output;

    private static byte[] Bank()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == VoiceBank.AssetId);
        directory.TryGetData(rom, entry, out var data);
        return data;
    }

    [RomFact]
    public void TheBankHolds588Clips()
    {
        var clips = VoiceBank.Read(Bank());

        Assert.Equal(588, clips.Count);
        Assert.All(clips, c => Assert.True(c.BlockCount > 0));
    }

    /// <summary>
    /// The anchor for the whole layout: the first clip begins in the byte immediately after the
    /// entry table. If the count or the entry size were wrong this would not land.
    /// </summary>
    [RomFact]
    public void TheFirstClipStartsWhereTheTableEnds()
    {
        var bank = Bank();
        var clips = VoiceBank.Read(bank);

        Assert.Equal(4 + clips.Count * 4, clips[0].Offset);
    }

    /// <summary>Every clip's own header agrees with the spacing of the entry table.</summary>
    [RomFact]
    public void EachClipsHeaderAgreesWithTheTableSpacing()
    {
        var bank = Bank();
        var clips = VoiceBank.Read(bank);

        foreach (var clip in clips.Take(clips.Count - 1))
            Assert.Equal(clip.StoredSize, VoiceBank.DeclaredSize(bank, clip));
    }

    /// <summary>
    /// The rate the player derives from the entry's flag byte matches the rate written inside the clip.
    /// </summary>
    [RomFact]
    public void TheFlagByteAgreesWithTheRateInEachClipHeader()
    {
        var bank = Bank();
        var clips = VoiceBank.Read(bank);

        foreach (var clip in clips)
        {
            int inHeader = (bank[clip.Offset + 6] << 8) | bank[clip.Offset + 7];
            Assert.Equal(clip.SampleRate, inHeader);
        }

        _out.WriteLine($"16 kHz: {clips.Count(c => c.SampleRate == VoiceBank.HighRate)}, " +
                       $"8 kHz: {clips.Count(c => c.SampleRate == VoiceBank.LowRate)}");
    }

    [RomFact]
    public void EveryClipCarriesTheMortMagic()
    {
        var bank = Bank();
        var clips = VoiceBank.Read(bank);

        foreach (var clip in clips)
            Assert.Equal(VoiceBank.Magic, bank.Skip(clip.Offset).Take(4));
    }

    /// <summary>
    /// The bank is the right size to be the game's dialogue, which is the point of finding it: far
    /// more speech than the whole sample bank holds of everything else.
    /// </summary>
    [RomFact]
    public void TheBankIsAboutFiftyMinutesOfAudio()
    {
        var clips = VoiceBank.Read(Bank());
        double minutes = clips.Sum(c => c.Seconds) / 60;

        _out.WriteLine($"total {minutes:0.0} minutes across {clips.Count} clips; " +
                       $"longest {clips.Max(c => c.Seconds):0.0}s");

        Assert.InRange(minutes, 40, 70);
    }
}
