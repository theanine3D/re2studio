using System;
using System.Collections.Generic;
using System.Text;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>One of the game's prerendered movies: a bare MPEG-1 video elementary stream.</summary>
public sealed record FmvStream(
    int AssetId,
    int StoredSize,
    int Width,
    int Height,
    int AspectRatioCode,
    int FrameRateCode,
    int BitRateCode,
    int VbvBufferSize,
    bool ConstrainedParameters,
    bool LoadIntraMatrix,
    bool LoadNonIntraMatrix,
    int Pictures,
    int Gops,
    string Name)
{
    /// <summary>Frame rates by code, from the MPEG-1 sequence header table. Index 0 is forbidden.</summary>
    public static readonly double[] FrameRates =
        { 0, 24000 / 1001.0, 24, 25, 30000 / 1001.0, 30, 50, 60000 / 1001.0, 60 };

    public double FramesPerSecond
        => FrameRateCode > 0 && FrameRateCode < FrameRates.Length ? FrameRates[FrameRateCode] : 0;

    /// <summary>How long the stream says it runs, at the rate it declares.</summary>
    public double Seconds => FramesPerSecond <= 0 ? 0 : Pictures / FramesPerSecond;

    /// <summary>
    /// The rate the console actually presents these at: 15 fps, exactly half the 30 the streams store
    /// and declare.
    /// </summary>
    public const double GameFrameRate = 15;

    /// <summary>How long the movie runs in game, which is what anyone playing it sees.</summary>
    public double GameSeconds => Pictures / GameFrameRate;

    /// <summary>Bits per second the stream declares. The code counts 400 bit/s units.</summary>
    public int DeclaredBitRate => BitRateCode == Mpeg1Sequence.VariableBitRate ? 0 : BitRateCode * 400;

    public override string ToString()
        => $"{(Name.Length > 0 ? Name : $"asset {AssetId}")} {Width}x{Height} {Seconds:0.00}s";
}

/// <summary>Finds and reads the sequence headers of the movies in a ROM.</summary>
public static class FmvIndex
{
    /// <summary>Does this asset open with an MPEG-1 sequence header?</summary>
    public static bool IsMovie(ReadOnlySpan<byte> data)
        => data.Length >= Mpeg1Sequence.HeaderSize && Mpeg1Sequence.StartsWith(data, Mpeg1Sequence.SequenceHeader);

    /// <summary>Every movie in the ROM, in asset order.</summary>
    public static IReadOnlyList<FmvStream> Build(RomFile rom, AssetDirectory directory)
    {
        var movies = new List<FmvStream>();

        foreach (var entry in directory.Entries)
        {
            if (!directory.TryGetData(rom, entry, out var data) || !IsMovie(data)) continue;
            if (Parse(entry.Index, data) is { } movie) movies.Add(movie);
        }

        return movies;
    }

    /// <summary>Reads one stream's sequence header and counts what follows it.</summary>
    public static FmvStream? Parse(int assetId, ReadOnlySpan<byte> data)
    {
        if (!IsMovie(data)) return null;

        var header = Mpeg1Sequence.Read(data);

        return new FmvStream(
            assetId, data.Length,
            header.Width, header.Height, header.AspectRatioCode, header.FrameRateCode,
            header.BitRateCode, header.VbvBufferSize, header.ConstrainedParameters,
            header.LoadIntraMatrix, header.LoadNonIntraMatrix,
            Mpeg1Sequence.Count(data, Mpeg1Sequence.PictureStart),
            Mpeg1Sequence.Count(data, Mpeg1Sequence.GroupStart),
            ReadName(data));
    }

    /// <summary>
    /// The name the movie was authored under, from the user-data block that follows the sequence
    /// header.
    /// </summary>
    public static string ReadName(ReadOnlySpan<byte> data)
    {
        int at = Mpeg1Sequence.Find(data, Mpeg1Sequence.UserData, 0, limit: 256);
        if (at < 0) return "";

        var text = new StringBuilder();
        for (int i = at + 4; i < data.Length && text.Length < 64; i++)
        {
            // The block runs to the next start code; the name itself ends at the newline after it.
            if (data[i] < 0x20) break;
            text.Append((char)data[i]);
        }

        return text.ToString();
    }
}
