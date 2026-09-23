using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Re2.Core.Assets;
using Re2.Core.Formats;

namespace Re2.Core.Video;

/// <summary>
/// Turns a video file into a stream the game will play, sized to fit the slot it is going into.
/// </summary>
public static class FmvEncoder
{
    public const int Width = 240, Height = 120;
    public const int FrameRateCode = 5;              // 30 fps
    public const int AspectRatioCode = 2;

    /// <summary>The range the search runs over, in kbit/s, bracketing what the cart itself uses.</summary>
    public const int LowestKilobits = 40, HighestKilobits = 900;

    public sealed record Result(byte[] Data, int Kilobits, bool Fits, int Budget, string Log);

    /// <summary>Is this a file the importer can take without converting it?</summary>
    public static bool IsElementaryStream(string path)
        => Path.GetExtension(path).Equals(".m2v", StringComparison.OrdinalIgnoreCase)
           || Path.GetExtension(path).Equals(".mpv", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks a stream against the profile the game plays, returning null when it is acceptable and
    /// the reason when it is not.
    /// </summary>
    public static string? Reject(ReadOnlySpan<byte> stream)
    {
        if (!FmvIndex.IsMovie(stream))
            return "That file does not start with an MPEG-1 sequence header, so it is not a video " +
                   "elementary stream. Export it as MPEG-1 video (.m2v), or import an .mp4 and let " +
                   "the editor convert it.";

        var header = Mpeg1Sequence.Read(stream);

        if (header.Width != Width || header.Height != Height)
            return $"The stream is {header.Width}x{header.Height}; the game's movies are " +
                   $"{Width}x{Height} and the decoder has no way to scale them.";

        if (header.FrameRateCode != FrameRateCode)
            return $"The stream declares frame rate code {header.FrameRateCode}; the game's movies " +
                   $"are all code {FrameRateCode} (30 fps).";

        if (header.LoadIntraMatrix || header.LoadNonIntraMatrix)
            return "The stream carries custom quantiser matrices. No retail movie does, so the " +
                   "decoder is not known to read them.";

        return null;
    }

    /// <summary>
    /// Brings a stream's header to the retail profile: a real declared bit rate, measured from the
    /// stream itself, and the VBV size every retail movie uses.
    /// </summary>
    public static void Conform(Span<byte> stream)
    {
        int bitRateCode = Mpeg1Sequence.RetailBitRateCode;

        // Measured from this stream, never handed in: passing the length of whatever the import
        // replaced would declare a rate for a different movie.
        int pictures = Mpeg1Sequence.Count(stream, Mpeg1Sequence.PictureStart);
        double seconds = pictures / FmvStream.FrameRates[FrameRateCode];

        if (seconds > 0.1)
        {
            // bit_rate counts 400 bit/s units, rounded up so the stream never claims to need less
            // than it does.
            double bitsPerSecond = stream.Length * 8 / seconds;
            bitRateCode = (int)Math.Ceiling(bitsPerSecond / 400);
            bitRateCode = Math.Clamp(bitRateCode, 1, Mpeg1Sequence.VariableBitRate - 1);
        }

        Mpeg1Sequence.SetRate(stream, bitRateCode, Mpeg1Sequence.RetailVbvBufferSize);
    }

    /// <summary>
    /// How many bytes naming a stream costs: the start code, the name, and the newline after it.
    /// </summary>
    public static int NameBlockLength(string name)
        => name.Length == 0 ? 0 : 4 + name.Length + 1;

    /// <summary>
    /// Puts a name into the stream, in the user-data block that follows the sequence header -- which is
    /// exactly where every retail movie keeps the name it was authored under.
    /// </summary>
    public static byte[] WithName(ReadOnlySpan<byte> stream, string name)
    {
        if (name.Length == 0 || !FmvIndex.IsMovie(stream)) return stream.ToArray();

        int headerEnd = Mpeg1Sequence.HeaderLength(stream);

        // Anything already there is replaced rather than added to: the block runs from just after
        // the sequence header to the next start code.
        int existingEnd = headerEnd;
        if (Mpeg1Sequence.StartsWith(stream[headerEnd..], Mpeg1Sequence.UserData))
        {
            existingEnd = stream.Length;
            for (int i = headerEnd + 4; i + 3 < stream.Length; i++)
                if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1) { existingEnd = i; break; }
        }

        // ASCII only: a name is text, and anything else could forge a start code inside the block.
        var text = new List<byte>(name.Length + 1);
        foreach (char c in name) text.Add(c is >= (char)0x20 and < (char)0x7F ? (byte)c : (byte)'_');
        text.Add(0x0A);                                  // the newline every retail name ends with

        var result = new byte[headerEnd + 4 + text.Count + (stream.Length - existingEnd)];
        var span = result.AsSpan();

        stream[..headerEnd].CopyTo(span);
        span[headerEnd] = 0;
        span[headerEnd + 1] = 0;
        span[headerEnd + 2] = 1;
        span[headerEnd + 3] = Mpeg1Sequence.UserData;
        text.CopyTo(result, headerEnd + 4);
        stream[existingEnd..].CopyTo(span[(headerEnd + 4 + text.Count)..]);

        return result;
    }

    /// <summary>
    /// Cuts a stream down to at most <paramref name="maxPictures"/> frames, at a group boundary, and
    /// closes it with the sequence-end code every retail movie ends with.
    /// </summary>
    public static byte[] TruncateToPictures(ReadOnlySpan<byte> stream, int maxPictures, out int kept)
    {
        kept = Mpeg1Sequence.Count(stream, Mpeg1Sequence.PictureStart);
        if (maxPictures <= 0 || kept <= maxPictures) return stream.ToArray();

        // Where each group starts, and how many pictures it holds.
        var groups = new List<int>();
        for (int i = 0; i + 4 <= stream.Length; i++)
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1 &&
                stream[i + 3] == Mpeg1Sequence.GroupStart)
                groups.Add(i);

        if (groups.Count == 0) return stream.ToArray();

        int running = 0, cut = -1;

        for (int g = 0; g < groups.Count; g++)
        {
            int end = g + 1 < groups.Count ? groups[g + 1] : stream.Length;
            int inGroup = Mpeg1Sequence.Count(stream[groups[g]..end], Mpeg1Sequence.PictureStart);

            // Keeping this group would overrun, so the cut goes at its start -- unless it is the
            // first, in which case a short movie beats an empty one.
            if (running + inGroup > maxPictures && g > 0) { cut = groups[g]; break; }

            running += inGroup;
        }

        if (cut < 0) return stream.ToArray();

        var result = new byte[cut + 4];
        stream[..cut].CopyTo(result);
        result[cut] = 0;
        result[cut + 1] = 0;
        result[cut + 2] = 1;
        result[cut + 3] = Mpeg1Sequence.SequenceEnd;

        kept = running;
        return result;
    }

    /// <summary>
    /// Converts <paramref name="source"/> and searches for the highest bit rate that fits
    /// <paramref name="budget"/> bytes.
    /// </summary>
    public static Result EncodeToFit(string source, int budget, string scratchDirectory,
                                     double maxSeconds = 0)
    {
        Directory.CreateDirectory(scratchDirectory);

        byte[]? best = null;
        int bestKilobits = 0;
        var log = new List<string>();

        int low = LowestKilobits, high = HighestKilobits;

        while (low <= high)
        {
            int kilobits = (low + high) / 2;
            string output = Path.Combine(scratchDirectory, $"fmv-{kilobits}.m2v");

            if (!Encode(source, output, kilobits, maxSeconds, out string error))
                return new Result(Array.Empty<byte>(), 0, false, budget, error);

            var data = File.ReadAllBytes(output);
            try { File.Delete(output); } catch (IOException) { /* scratch */ }

            log.Add($"{kilobits} kbit/s -> {data.Length:N0} bytes");

            if (data.Length <= budget)
            {
                best = data;
                bestKilobits = kilobits;
                low = kilobits + 1;
            }
            else high = kilobits - 1;
        }

        if (best is null)
        {
            // Nothing fits: hand back the smallest so the caller can say by how much it missed.
            string output = Path.Combine(scratchDirectory, "fmv-lowest.m2v");
            if (!Encode(source, output, LowestKilobits, maxSeconds, out string error))
                return new Result(Array.Empty<byte>(), 0, false, budget, error);

            best = File.ReadAllBytes(output);
            bestKilobits = LowestKilobits;
            try { File.Delete(output); } catch (IOException) { /* scratch */ }
        }

        return new Result(best, bestKilobits, best.Length <= budget, budget, string.Join("; ", log));
    }

    /// <summary>One conversion, at a fixed bit rate, to the profile the game plays.</summary>
    public static bool Encode(string source, string output, int kilobits, double maxSeconds,
                              out string error)
    {
        // The GOP and B-frame settings match what the retail streams use: roughly fifteen pictures
        // to a group, two B pictures between anchors.
        var arguments = new List<string>
        {
            "-y", "-v", "error",
            "-i", source,
            "-an",                                   // an elementary video stream carries no audio
            "-c:v", "mpeg1video",
            "-s", $"{Width}x{Height}",
            "-r", "30",
            "-aspect", "2:1",
            "-g", "15",
            "-bf", "2",
            "-b:v", $"{kilobits}k",
            "-maxrate", $"{kilobits}k",
            "-bufsize", $"{Mpeg1Sequence.RetailVbvBufferSize * 16384 / 1000}k",
            "-f", "mpeg1video",
        };

        // Cut to the slot's length before anything else.
        if (maxSeconds > 0) { arguments.Add("-t"); arguments.Add(maxSeconds.ToString("0.###", CultureInfo.InvariantCulture)); }

        arguments.Add(output);

        return Ffmpeg.Run(arguments, out error);
    }
}
