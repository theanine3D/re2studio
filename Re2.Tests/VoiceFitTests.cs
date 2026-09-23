using System;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Emulation;
using Re2.Core.Formats;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Fitting a replacement clip into an existing slot.</summary>
public class VoiceFitTests
{
    private readonly ITestOutputHelper _out;

    public VoiceFitTests(ITestOutputHelper output) => _out = output;

    private static byte[] Bank()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == VoiceBank.AssetId);
        directory.TryGetData(rom, entry, out var data);
        return data;
    }

    private static MortCodec.Padder PadderFor(MipsCpu cpu)
        => (clip, from, to) => MortCodec.PadWithSilence(cpu, clip, from, to);

    /// <summary>A shorter clip is padded, and the slot keeps its own length and position.</summary>
    [RomFact]
    public void AShorterClipIsPaddedWithSilenceAndTheSlotDoesNotMove()
    {
        var bank = Bank();
        var clips = VoiceBank.Read(bank);
        var cpu = GameCode.Load(TestRom.Rom);

        var slot = clips.First(c => c.BlockCount is > 300 and < 600);
        var shorter = MortCodec.Silence(slot.BlockCount / 3, slot.SampleRate);

        var rebuilt = VoiceBank.ReplaceInPlace(bank, slot, shorter, PadderFor(cpu));
        var after = VoiceBank.Read(rebuilt);

        Assert.Equal(bank.Length, rebuilt.Length);
        Assert.Equal(slot.Offset, after[slot.Index].Offset);
        Assert.Equal(slot.StoredSize, after[slot.Index].StoredSize);
        Assert.Equal(slot.BlockCount, after[slot.Index].BlockCount);

        // It decodes to the full slot length, all of it silent.
        var samples = MortCodec.Decode(cpu, rebuilt.AsSpan(slot.Offset, slot.StoredSize), slot.BlockCount);
        Assert.Equal(slot.SampleCount, samples.Length);
        Assert.All(samples, s => Assert.Equal(0, s));

        _out.WriteLine($"slot {slot.Index}: {slot.Seconds:0.00}s kept, replacement was " +
                       $"{shorter.Length} bytes for a third of the length");
    }

    /// <summary>Every other clip is untouched, which is the whole point of fitting in place.</summary>
    [RomFact]
    public void NothingElseInTheBankChanges()
    {
        var bank = Bank();
        var clips = VoiceBank.Read(bank);
        var cpu = GameCode.Load(TestRom.Rom);

        var slot = clips.First(c => c.BlockCount is > 300 and < 600);
        var rebuilt = VoiceBank.ReplaceInPlace(bank, slot, MortCodec.Silence(10, slot.SampleRate), PadderFor(cpu));

        for (int i = 0; i < bank.Length; i++)
        {
            bool inSlot = i >= slot.Offset && i < slot.Offset + slot.StoredSize;
            if (!inSlot && bank[i] != rebuilt[i])
                Assert.Fail($"byte 0x{i:X} changed but is outside slot {slot.Index}");
        }
    }

    /// <summary>A clip that runs longer than its slot is refused, and the message says by how much.</summary>
    [RomFact]
    public void ALongerClipIsRefusedWithTheOverrunNamed()
    {
        var bank = Bank();
        var clips = VoiceBank.Read(bank);
        var cpu = GameCode.Load(TestRom.Rom);

        var slot = clips.First(c => c.BlockCount is > 100 and < 200);
        var tooLong = MortCodec.Silence(slot.BlockCount + 160, slot.SampleRate);   // 1.6s over at 16 kHz

        var error = Assert.Throws<InvalidDataException>(
            () => VoiceBank.ReplaceInPlace(bank, slot, tooLong, PadderFor(cpu)));

        _out.WriteLine(error.Message);

        Assert.Contains("too long", error.Message);
        Assert.Contains("1.60s over", error.Message);
    }

    /// <summary>
    /// The separate byte limit: a clip of the right duration can still be too large, because the
    /// original may have been stored more compactly than a re-encoding of the same length.
    /// </summary>
    [RomFact]
    public void AClipThatFitsTheDurationButNotTheBytesIsRefused()
    {
        var bank = Bank();
        var clips = VoiceBank.Read(bank);
        var cpu = GameCode.Load(TestRom.Rom);

        // A slot the cart stored compactly: far fewer bytes than 32.5 per block.
        var slot = clips.First(c => c.BlockCount > 200 && c.StoredSize < c.BlockCount * 20);

        // Same number of blocks, but every one a normal block at the full 260 bits.
        var blocks = Enumerable.Range(0, slot.BlockCount).Select(_ => MortCodec.Block.Zero()).ToList();
        var fat = MortCodec.WriteBlocks(blocks, slot.SampleRate);

        Assert.True(fat.Length > slot.StoredSize);

        var error = Assert.Throws<InvalidDataException>(
            () => VoiceBank.ReplaceInPlace(bank, slot, fat, PadderFor(cpu)));

        _out.WriteLine(error.Message);
        Assert.Contains("does not fit", error.Message);
    }

    [RomFact]
    public void AnExactLengthReplacementNeedsNoPadding()
    {
        var bank = Bank();
        var clips = VoiceBank.Read(bank);
        var cpu = GameCode.Load(TestRom.Rom);

        var slot = clips.First(c => c.BlockCount is > 300 and < 600);
        var exact = MortCodec.Silence(slot.BlockCount, slot.SampleRate);

        var rebuilt = VoiceBank.ReplaceInPlace(bank, slot, exact, PadderFor(cpu));
        var after = VoiceBank.Read(rebuilt);

        Assert.Equal(slot.BlockCount, after[slot.Index].BlockCount);
        Assert.Equal(slot.Offset, after[slot.Index].Offset);
    }
}
