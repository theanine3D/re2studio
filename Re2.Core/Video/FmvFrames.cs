using System;
using System.Collections.Generic;
using System.IO;
using Re2.Core.Assets;
using Re2.Core.Formats;

namespace Re2.Core.Video;

/// <summary>Decodes a movie to RGBA frames, for the editor to show.</summary>
public static class FmvFrames
{
    public sealed record Movie(IReadOnlyList<byte[]> Frames, int Width, int Height, double FramesPerSecond)
    {
        public double Seconds => FramesPerSecond <= 0 ? 0 : Frames.Count / FramesPerSecond;

        public static readonly Movie Empty = new(Array.Empty<byte[]>(), 0, 0, 0);
    }

    /// <summary>Decodes an elementary stream.</summary>
    public static Movie Decode(ReadOnlySpan<byte> stream, out string error)
    {
        error = "";

        if (!FmvIndex.IsMovie(stream)) { error = "That is not an MPEG-1 stream."; return Movie.Empty; }
        if (!Ffmpeg.Available) { error = Ffmpeg.Missing; return Movie.Empty; }

        var header = Mpeg1Sequence.Read(stream);
        int width = header.Width, height = header.Height;
        if (width <= 0 || height <= 0) { error = "The stream declares no picture size."; return Movie.Empty; }

        string scratch = Path.Combine(Path.GetTempPath(), "re2-play-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        string input = Path.Combine(scratch, "in.m2v");
        string output = Path.Combine(scratch, "out.rgba");

        try
        {
            File.WriteAllBytes(input, stream.ToArray());

            // passthrough, or ffmpeg conforms the output to a constant frame rate and duplicates a
            // frame: a raw elementary stream carries no timing, so it assumes 25 fps and pads a
            // 30 fps movie out. Measured -- ffprobe counts ten frames where this produced eleven.
            bool decoded = Ffmpeg.Run(new[] { "-y", "-v", "error", "-i", input,
                                              "-fps_mode", "passthrough",
                                              "-f", "rawvideo", "-pix_fmt", "rgba", output }, out error);

            // ffmpeg before 5.1 (Ubuntu 22.04 ships 4.4) spells the same option -vsync.
            if (!decoded && error.Contains("fps_mode"))
                decoded = Ffmpeg.Run(new[] { "-y", "-v", "error", "-i", input,
                                             "-vsync", "passthrough",
                                             "-f", "rawvideo", "-pix_fmt", "rgba", output }, out error);

            if (!decoded) return Movie.Empty;

            var raw = File.ReadAllBytes(output);
            int stride = width * height * 4;

            if (stride <= 0 || raw.Length < stride)
            {
                error = "ffmpeg produced no frames.";
                return Movie.Empty;
            }

            var frames = new List<byte[]>(raw.Length / stride);
            for (int at = 0; at + stride <= raw.Length; at += stride)
                frames.Add(raw.AsSpan(at, stride).ToArray());

            double fps = FmvStream.FrameRates[
                Math.Clamp(header.FrameRateCode, 0, FmvStream.FrameRates.Length - 1)];

            return new Movie(frames, width, height, fps > 0 ? fps : 30);
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return Movie.Empty;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { /* scratch */ }
        }
    }
}
