using System;
using System.IO;
using Re2.Core.Save;
using Xunit;

namespace Re2.Tests;

/// <summary>
/// Between Project64 and Mupen64 the SRAM bytes themselves are identical; the only difference is
/// the uninitialised tail Mupen flushes to disk. These tests pin that down.
/// </summary>
public sealed class N64SaveConverterTests
{
    // ---- the bytes themselves ---------------------------------------------

    [Fact]
    public void SramWithoutATailIsByteForByteIdenticalBothWays()
    {
        // Random data will not contain a long trailing run of one repeated word, so no
        // normalisation should apply and the file should pass through untouched.
        var sram = new byte[N64SaveConverter.SramSize];
        new Random(2).NextBytes(sram);
        sram[^2] = 0; sram[^1] = 0;

        var toP64 = N64SaveConverter.Convert(sram, EmulatorFormat.Mupen64, EmulatorFormat.Project64);
        var backToMupen = N64SaveConverter.Convert(toP64.Data, EmulatorFormat.Project64, EmulatorFormat.Mupen64);

        Assert.Equal(sram, toP64.Data);
        Assert.Equal(sram, backToMupen.Data);
    }

    [Fact]
    public void MupenUninitialisedTailIsCleared()
    {
        // Mupen flushed its malloc'd buffer, leaving a repeating 16-bit value at the end, which
        // Project64 never writes. Converting to P64 must clear that tail or RE2 refuses the save.
        var sram = new byte[N64SaveConverter.SramSize];
        sram[0] = 0x42;
        for (int i = 0x7A40; i < sram.Length; i += 2) { sram[i] = 0x01; sram[i + 1] = 0xF8; }

        var result = N64SaveConverter.Convert(sram, EmulatorFormat.Mupen64, EmulatorFormat.Project64);

        Assert.Equal(0x42, result.Data[0]);
        for (int i = 0x7A40; i < result.Data.Length; i++)
            Assert.Equal(0, result.Data[i]);
    }

    [Fact]
    public void AShortRepeatedRunIsLeftAlone()
    {
        // Real save data can contain short repeats; only a long trailing run is treated as garbage.
        var sram = new byte[N64SaveConverter.SramSize];
        for (int i = 0x7F00; i < 0x7F00 + 64; i += 2) { sram[i] = 0x01; sram[i + 1] = 0xF8; }

        var result = N64SaveConverter.Convert(sram, EmulatorFormat.Mupen64, EmulatorFormat.Project64);

        for (int i = 0x7F00; i < 0x7F00 + 64; i += 2)
        {
            Assert.Equal(0x01, result.Data[i]);
            Assert.Equal(0xF8, result.Data[i + 1]);
        }
    }

    [Fact]
    public void AZeroTailIsNotTouchedAndProject64SavesAreUnchanged()
    {
        // Converting from Project64 must not alter the file at all: P64 already writes it cleanly.
        var sram = new byte[N64SaveConverter.SramSize];
        new Random(11).NextBytes(sram);
        sram[^2] = 0; sram[^1] = 0;

        var result = N64SaveConverter.Convert(sram, EmulatorFormat.Project64, EmulatorFormat.Mupen64);

        Assert.Equal(sram, result.Data);
    }

    [Fact]
    public void TheInputIsNotModifiedInPlace()
    {
        var sram = new byte[N64SaveConverter.SramSize];
        for (int i = 0x7A40; i < sram.Length; i += 2) { sram[i] = 0x01; sram[i + 1] = 0xF8; }
        var original = (byte[])sram.Clone();

        N64SaveConverter.Convert(sram, EmulatorFormat.Mupen64, EmulatorFormat.Project64);

        Assert.Equal(original, sram);
    }

    [Fact]
    public void AWrongSizedFileIsRejected()
    {
        Assert.Throws<InvalidDataException>(
            () => N64SaveConverter.Convert(new byte[0x200], EmulatorFormat.Project64, EmulatorFormat.Mupen64));
    }

    // ---- files ------------------------------------------------------------

    [Fact]
    public void ConvertFileWritesANewFileAndLeavesTheInput()
    {
        string folder = Path.Combine(Path.GetTempPath(), "re2-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string input = Path.Combine(folder, "re2.sra");
            var original = new byte[N64SaveConverter.SramSize];
            new Random(3).NextBytes(original);
            File.WriteAllBytes(input, original);

            string output = Path.Combine(folder, "re2-p64.sra");
            var result = N64SaveConverter.ConvertFile(input, EmulatorFormat.Mupen64, EmulatorFormat.Project64, output);

            Assert.True(File.Exists(output));
            Assert.Equal(original, File.ReadAllBytes(input));      // untouched
            Assert.Equal(N64SaveConverter.SramSize, result.Data.Length);
            Assert.Contains("wrote", result.Log);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheDefaultOutputNameNamesTheTargetAndCannotCollideWithTheInput()
    {
        // SRAM -> SRAM keeps the .sra extension, so the target must be named distinctly or it would
        // be the same path as the file being read.
        string name = N64SaveConverter.DefaultOutputPath(
            Path.Combine("saves", "re2.sra"), EmulatorFormat.Project64);
        Assert.Equal(Path.Combine("saves", "re2-p64.sra"), name);
        Assert.NotEqual(Path.Combine("saves", "re2.sra"), name);

        name = N64SaveConverter.DefaultOutputPath(
            Path.Combine("saves", "re2.sra"), EmulatorFormat.Mupen64);
        Assert.Equal(Path.Combine("saves", "re2-mupen.sra"), name);
    }

    [Fact]
    public void ConvertFileDefaultsToBesideTheInputAndNeverOverwritesIt()
    {
        string folder = Path.Combine(Path.GetTempPath(), "re2-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string input = Path.Combine(folder, "re2.sra");
            var original = new byte[N64SaveConverter.SramSize];
            new Random(5).NextBytes(original);
            File.WriteAllBytes(input, original);

            N64SaveConverter.ConvertFile(input, EmulatorFormat.Mupen64, EmulatorFormat.Project64);

            string expected = Path.Combine(folder, "re2-p64.sra");
            Assert.True(File.Exists(expected));
            Assert.Equal(original, File.ReadAllBytes(input));      // untouched
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
