using System;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Emulation;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Decoding the voice bank by running the game's own decoder.</summary>
public class MortCodecTests
{
    private readonly ITestOutputHelper _out;

    public MortCodecTests(ITestOutputHelper output) => _out = output;

    private static (byte[] Bank, System.Collections.Generic.IReadOnlyList<VoiceClip> Clips) Bank()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == VoiceBank.AssetId);
        directory.TryGetData(rom, entry, out var data);
        return (data, VoiceBank.Read(data));
    }

    private static short[] Decode(MipsCpu cpu, byte[] bank, VoiceClip clip)
        => MortCodec.Decode(cpu, bank.AsSpan(clip.Offset, clip.StoredSize), clip.BlockCount);

    /// <summary>Fraction of sign changes: near 0.5 is noise, voiced speech is far below.</summary>
    private static double ZeroCrossingRate(short[] samples)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
            if (samples[i - 1] < 0 != samples[i] < 0) crossings++;
        return (double)crossings / samples.Length;
    }

    [RomFact]
    public void AClipDecodesToTheLengthItsHeaderPromises()
    {
        var (bank, clips) = Bank();
        var cpu = GameCode.Load(TestRom.Rom);

        var clip = clips.First(c => c.BlockCount is > 100 and < 400);
        var samples = Decode(cpu, bank, clip);

        Assert.Equal(clip.BlockCount * MortCodec.SamplesPerBlock, samples.Length);
        Assert.Equal(clip.SampleCount, samples.Length);
    }

    /// <summary>
    /// The output is audio: it uses the range, and it is not the flat sign-flipping of noise, which
    /// is what a wrong bit position or a mis-filled ring would give.
    /// </summary>
    [RomFact]
    public void WhatComesOutIsAudioRatherThanNoiseOrSilence()
    {
        var (bank, clips) = Bank();
        var cpu = GameCode.Load(TestRom.Rom);

        foreach (var clip in clips.Where(c => c.BlockCount is > 200 and < 500).Take(5))
        {
            var samples = Decode(cpu, bank, clip);

            int peak = samples.Max(s => Math.Abs((int)s));
            double rate = ZeroCrossingRate(samples);

            _out.WriteLine($"clip {clip.Index}: peak {peak}, zero-crossing {rate:0.000}");

            Assert.True(peak > 1000, $"clip {clip.Index} is effectively silent (peak {peak})");
            Assert.True(rate < 0.35, $"clip {clip.Index} looks like noise (zero-crossing {rate:0.000})");
        }
    }

    /// <summary>
    /// The decoder carries a bit position and run counters between blocks, so a clip decoded after
    /// another has to come out the same as one decoded first.
    /// </summary>
    [RomFact]
    public void AClipDecodesTheSameWhicheverClipPrecededIt()
    {
        var (bank, clips) = Bank();
        var cpu = GameCode.Load(TestRom.Rom);

        var target = clips.First(c => c.BlockCount is > 100 and < 300);
        var other = clips.Last(c => c.BlockCount is > 100 and < 300);

        var alone = Decode(cpu, bank, target);
        Decode(cpu, bank, other);
        var afterAnother = Decode(cpu, bank, target);

        Assert.Equal(alone, afterAnother);
    }

    /// <summary>A fresh interpreter must give the same answer as a reused one.</summary>
    [RomFact]
    public void AReusedInterpreterDecodesLikeAFreshOne()
    {
        var (bank, clips) = Bank();
        var clip = clips.First(c => c.BlockCount is > 100 and < 300);

        var fresh = Decode(GameCode.Load(TestRom.Rom), bank, clip);

        var reused = GameCode.Load(TestRom.Rom);
        Decode(reused, bank, clips.First(c => c.BlockCount > 500));
        var again = Decode(reused, bank, clip);

        Assert.Equal(fresh, again);
    }

    /// <summary>The 8 kHz clips go through the same decoder; only their playback rate differs.</summary>
    [RomFact]
    public void TheLowRateClipsDecodeToo()
    {
        var (bank, clips) = Bank();
        var cpu = GameCode.Load(TestRom.Rom);

        var low = clips.Where(c => c.SampleRate == VoiceBank.LowRate).Take(3).ToList();
        Assert.NotEmpty(low);

        foreach (var clip in low)
        {
            var samples = Decode(cpu, bank, clip);
            Assert.Equal(clip.SampleCount, samples.Length);
            Assert.True(samples.Max(s => Math.Abs((int)s)) > 100, $"clip {clip.Index} decoded silent");
        }
    }
}
