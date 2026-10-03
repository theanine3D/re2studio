using System;
using System.IO;

namespace Re2.Core.Save;

/// <summary>The two emulators whose save layouts differ.</summary>
public enum EmulatorFormat
{
    Project64,
    Mupen64,
}

/// <summary>
/// Converts a Resident Evil 2 SRAM save file between Project64 and Mupen64 layouts.
///
/// Both emulators store the game's save data exactly as the cartridge sees it -- big-endian, no
/// header, no per-file metadata -- so the bytes themselves never need reordering. The only
/// difference is at the tail: Mupen allocates its 0x8000-byte buffer with malloc and writes the
/// whole thing, so bytes the game never touched hold whatever was in that memory. Project64
/// zero-initialises the buffer, so it never writes them. RE2 refuses a save carrying that
/// uninitialised tail, so it is cleared when converting away from Mupen.
/// </summary>
public static class N64SaveConverter
{
    /// <summary>256 Kbit SRAM, the size RE2 uses.</summary>
    public const int SramSize = 0x8000;

    /// <summary>The extension both emulators use for SRAM saves.</summary>
    public const string SramExtension = ".sra";

    /// <summary>What a conversion produced, plus what was changed along the way.</summary>
    public sealed record Result(byte[] Data, EmulatorFormat From, EmulatorFormat To, string Log);

    /// <summary>
    /// Converts the whole contents of <paramref name="input"/> and returns the bytes to write.
    /// </summary>
    public static Result Convert(byte[] input, EmulatorFormat from, EmulatorFormat to)
    {
        if (input.Length != SramSize)
            throw new InvalidDataException(
                $"A save should be {SramSize:N0} bytes (32 KB) but this one is {input.Length:N0}.");

        // Work on a copy, since normalisation edits the bytes in place.
        var output = (byte[])input.Clone();

        // Mupen flushes its malloc'd buffer to disk, leaving a single 16-bit value repeating for
        // kilobytes past the game's real data. Project64 never writes that, and RE2 refuses a save
        // that carries it, so clear it on the way out of Mupen.
        if (from == EmulatorFormat.Mupen64 && to != EmulatorFormat.Mupen64)
            ClearTrailingFill(output);

        string log =
            $"SRAM ({from} -> {to})\n" +
            $"input  {input.Length:N0} bytes\n" +
            $"output {output.Length:N0} bytes, extension {SramExtension}";

        return new Result(output, from, to, log);
    }

    /// <summary>
    /// Converts an existing file, writing the result beside it under a new name. The input is never
    /// touched, matching the "save as" behaviour of the rest of the tool.
    /// </summary>
    public static Result ConvertFile(string inputPath, EmulatorFormat from, EmulatorFormat to,
                                     string? outputPath = null)
    {
        var input = File.ReadAllBytes(inputPath);
        var result = Convert(input, from, to);

        outputPath ??= DefaultOutputPath(inputPath, to);
        File.WriteAllBytes(outputPath, result.Data);

        return result with { Log = result.Log + $"\nwrote {Path.GetFullPath(outputPath)}" };
    }

    /// <summary>
    /// The output name: the input's stem, the target emulator and the .sra extension, written beside
    /// the input. The target is named so it can never collide with the file being read.
    /// </summary>
    public static string DefaultOutputPath(string inputPath, EmulatorFormat to)
    {
        string stem = Path.GetFileNameWithoutExtension(inputPath);
        string suffix = to == EmulatorFormat.Project64 ? "-p64" : "-mupen";
        return Path.Combine(Path.GetDirectoryName(inputPath) ?? ".", stem + suffix + SramExtension);
    }

    /// <summary>
    /// Clears a trailing run of one repeated, non-zero 16-bit value: the signature of uninitialised
    /// memory that Mupen happened to flush. A run has to be long to qualify, so genuine save data
    /// that happens to repeat is left alone.
    /// </summary>
    private static void ClearTrailingFill(byte[] data)
    {
        const int minimumRun = 256;    // bytes; far longer than any real repeated field

        if (data.Length < 2) return;

        // The 16-bit value the file ends on, big-endian (the N64 reads these bytes as words).
        int fill = (data[^2] << 8) | data[^1];
        if (fill == 0) return;         // a zero tail is already what Project64 writes

        int start = data.Length;
        while (start >= 2)
        {
            int value = (data[start - 2] << 8) | data[start - 1];
            if (value != fill) break;
            start -= 2;
        }

        if (data.Length - start >= minimumRun)
            Array.Clear(data, start, data.Length - start);
    }
}
