using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Re2.Studio;

/// <summary>Desktop services on Linux, done through the standard command-line tools.</summary>
public static class LinuxDesktop
{
    /// <summary>True when a program of this name is on the PATH.</summary>
    public static bool Has(string program)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return false;

        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
                   .Any(dir => File.Exists(Path.Combine(dir, program)));
    }

    private static ProcessStartInfo Start(string program, IEnumerable<string> args, bool input = false,
                                          bool output = false)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = input,
            RedirectStandardOutput = output,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    // ---- file dialog --------------------------------------------------------

    /// <summary>Shows an Open dialog with zenity or kdialog; null when cancelled or neither is installed.</summary>
    public static string? OpenFile(string title, string filter, string? initialDirectory)
    {
        var filters = ParseFilter(filter);
        string start = initialDirectory is { Length: > 0 } && Directory.Exists(initialDirectory)
            ? initialDirectory.TrimEnd('/') + "/"
            : "";

        if (Has("zenity"))
        {
            var args = new List<string> { "--file-selection", "--title=" + title };
            if (start.Length > 0) args.Add("--filename=" + start);
            foreach (var (label, patterns) in filters)
                args.Add("--file-filter=" + label + " | " + string.Join(' ', patterns));
            return Ask("zenity", args);
        }

        if (Has("kdialog"))
        {
            string kdFilter = string.Join('\n', filters.Select(f => string.Join(' ', f.Patterns) + "|" + f.Label));
            return Ask("kdialog", new[] { "--title", title, "--getopenfilename", start.Length > 0 ? start : ".", kdFilter });
        }

        return null;
    }

    /// <summary>Why the Open dialog cannot appear, or null when it can.</summary>
    public static string? DialogProblem =>
        Has("zenity") || Has("kdialog") ? null : "Install zenity or kdialog to get a file browser; paths can still be typed.";

    private static string? Ask(string program, IEnumerable<string> args)
    {
        try
        {
            using var process = Process.Start(Start(program, args, output: true));
            if (process is null) return null;

            string chosen = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && chosen.Length > 0 ? chosen : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Turns a Win32 filter ("Label\0*.a;*.b\0...") into (label, patterns) pairs.</summary>
    public static List<(string Label, string[] Patterns)> ParseFilter(string filter)
    {
        var parts = filter.Split('\0');
        var result = new List<(string, string[])>();

        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (parts[i].Length == 0) break;
            result.Add((parts[i], parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries)));
        }

        return result;
    }

    // ---- clipboard ----------------------------------------------------------

    /// <summary>Puts a PNG on the clipboard with wl-copy (Wayland) or xclip (X11).</summary>
    public static void CopyPng(byte[] png)
    {
        bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        string program;
        string[] args;
        if (wayland && Has("wl-copy")) { program = "wl-copy"; args = new[] { "--type", "image/png" }; }
        else if (Has("xclip")) { program = "xclip"; args = new[] { "-selection", "clipboard", "-t", "image/png", "-i" }; }
        else throw new PlatformNotSupportedException("Copying an image needs wl-copy (Wayland) or xclip (X11) installed.");

        // Both keep running in the background to serve the clipboard, so this does not wait for them.
        var process = Process.Start(Start(program, args, input: true))
                      ?? throw new InvalidOperationException($"Could not start {program}.");

        using (var stdin = process.StandardInput.BaseStream) stdin.Write(png);
    }

    // ---- audio --------------------------------------------------------------

    /// <summary>The first installed player that can play a WAV file, or null.</summary>
    public static string? AudioPlayerProgram =>
        new[] { "paplay", "pw-play", "aplay" }.FirstOrDefault(Has);

    /// <summary>Starts playing a WAV file.</summary>
    public static Process? PlayWav(string path)
    {
        string? program = AudioPlayerProgram;
        if (program is null) return null;

        var args = program == "aplay" ? new[] { "-q", path } : new[] { path };
        return Process.Start(Start(program, args));
    }
}
