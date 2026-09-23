using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Re2.Core.Video;

/// <summary>Runs ffmpeg, when it is available.</summary>
public static class Ffmpeg
{
    /// <summary>Somewhere to look besides the PATH, set by the host if the user configures one.</summary>
    public static string? ConfiguredPath { get; set; }

    private static readonly string[] Fallbacks =
    {
        @"C:\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
    };

    /// <summary>The ffmpeg to use, or null when there is none.</summary>
    public static string? Find()
    {
        if (ConfiguredPath is { Length: > 0 } configured && File.Exists(configured)) return configured;

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;

            // Only this OS's own build: under WSL the Windows PATH is inherited, and a Windows
            // ffmpeg.exe cannot open Linux paths such as /tmp.
            foreach (string name in OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe", "ffmpeg" } : new[] { "ffmpeg" })
            {
                string candidate;
                try { candidate = Path.Combine(directory.Trim(), name); }
                catch (ArgumentException) { continue; }        // a malformed PATH entry

                if (File.Exists(candidate)) return candidate;
            }
        }

        return OperatingSystem.IsWindows() ? Fallbacks.FirstOrDefault(File.Exists) : null;
    }

    public static bool Available => Find() is not null;

    /// <summary>What to tell someone who needs it and has not got it.</summary>
    public const string Missing =
        "This needs ffmpeg, which converts the video into the format the game uses. Install it and " +
        "make sure it is on your PATH (on Linux, the distribution's ffmpeg package), then try again. " +
        "Importing an .m2v file needs nothing extra.";

    /// <summary>
    /// Runs ffmpeg and returns whether it succeeded, with whatever it said on stderr -- which is
    /// where ffmpeg writes everything, including its errors.
    /// </summary>
    public static bool Run(IEnumerable<string> arguments, out string output, int timeoutSeconds = 300)
    {
        string? exe = Find();
        if (exe is null) { output = Missing; return false; }

        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process is null) { output = "ffmpeg would not start."; return false; }

        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try { process.Kill(true); } catch (Exception) { /* already gone */ }
            output = $"ffmpeg did not finish within {timeoutSeconds} seconds.";
            return false;
        }

        output = error.Trim();
        return process.ExitCode == 0;
    }
}
