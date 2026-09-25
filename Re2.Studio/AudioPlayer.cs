using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Re2.Studio;

/// <summary>Plays a decoded sample so it can be previewed in the window.</summary>
public static class AudioPlayer
{
    private const uint SndAsync = 0x0001;      // return immediately, keep playing
    private const uint SndNoDefault = 0x0002;  // silence rather than the system ding on failure
    private const uint SndMemory = 0x0004;     // the pointer is WAV bytes, not a filename
    private const uint SndLoop = 0x0008;
    private const uint SndPurge = 0x0040;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(IntPtr data, IntPtr module, uint flags);

    /// <summary>Keeps the WAV bytes pinned for as long as the device may be reading them.</summary>
    private static GCHandle _pinned;

    // Linux: the player process, the temp WAV it reads, and a generation counter that ends a loop.
    private static Process? _process;
    private static string? _tempFile;
    private static int _generation;

    private static readonly Stopwatch Clock = new();
    private static double _duration;
    private static bool _looping;

    /// <summary>Which sample is playing, so the panel can show it. -1 when stopped.</summary>
    public static int PlayingIndex { get; private set; } = -1;

    public static bool IsPlaying
    {
        get
        {
            if (PlayingIndex < 0) return false;
            if (_looping) return true;

            // Nothing reports completion, so treat the clock running past the sample as finished.
            // Only the UI state ends here: the device may still be playing, because output starts
            // late (tens to hundreds of milliseconds, more on Bluetooth), and purging now would
            // silence a short sample before any of it was heard. The buffer stays pinned until
            // the next Play or Stop.
            if (Clock.Elapsed.TotalSeconds >= _duration) { Finish(); return false; }
            return true;
        }
    }

    /// <summary>How far into the sample playback has got, in seconds.</summary>
    public static double Position => PlayingIndex < 0
        ? 0
        : _looping && _duration > 0
            ? Clock.Elapsed.TotalSeconds % _duration
            : Math.Min(Clock.Elapsed.TotalSeconds, _duration);

    /// <summary>True when this platform can play audio at all.</summary>
    public static bool Supported => OperatingSystem.IsWindows() ||
                                    (OperatingSystem.IsLinux() && LinuxDesktop.AudioPlayerProgram is not null);

    /// <summary>Why playback is unavailable here.</summary>
    public static string Unavailable => OperatingSystem.IsLinux()
        ? "Playback needs paplay, pw-play or aplay installed."
        : "Playback is not supported on this platform.";

    /// <summary>Starts playing a WAV held in memory.</summary>
    public static bool Play(byte[] wav, int index, double durationSeconds, bool loop)
    {
        Stop();
        if (!Supported || wav.Length == 0) return false;

        bool started = OperatingSystem.IsWindows() ? PlayWindows(wav, loop) : PlayLinux(wav, loop);
        if (!started) return false;

        PlayingIndex = index;
        _duration = durationSeconds;
        _looping = loop;
        Clock.Restart();
        return true;
    }

    private static bool PlayWindows(byte[] wav, bool loop)
    {
        try
        {
            _pinned = GCHandle.Alloc(wav, GCHandleType.Pinned);
            uint flags = SndMemory | SndAsync | SndNoDefault | (loop ? SndLoop : 0);

            if (PlaySound(_pinned.AddrOfPinnedObject(), IntPtr.Zero, flags)) return true;
            Release();
            return false;
        }
        catch (Exception)
        {
            Release();
            return false;
        }
    }

    private static bool PlayLinux(byte[] wav, bool loop)
    {
        try
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"re2studio-{Environment.ProcessId}-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(_tempFile, wav);

            _process = LinuxDesktop.PlayWav(_tempFile);
            if (_process is null) { Release(); return false; }

            if (loop)
            {
                // The players have no loop option, so restart the file each time it finishes.
                int generation = _generation;
                string file = _tempFile;
                var thread = new Thread(() =>
                {
                    while (true)
                    {
                        var current = _process;
                        try { current?.WaitForExit(); } catch (Exception) { return; }
                        if (generation != _generation) return;
                        var next = LinuxDesktop.PlayWav(file);
                        if (next is null) return;

                        // Stopped while the next pass was starting: do not leave it playing.
                        if (generation != _generation) { try { next.Kill(); } catch (Exception) { } return; }
                        _process = next;
                    }
                }) { IsBackground = true, Name = "audio loop" };
                thread.Start();
            }

            return true;
        }
        catch (Exception)
        {
            Release();
            return false;
        }
    }

    public static void Stop()
    {
        if (OperatingSystem.IsWindows())
        {
            try { PlaySound(IntPtr.Zero, IntPtr.Zero, SndPurge); } catch (Exception) { /* nothing to stop */ }
        }

        Release();
        Finish();
    }

    /// <summary>Marks playback over without touching the device.</summary>
    private static void Finish()
    {
        PlayingIndex = -1;
        _looping = false;
        _duration = 0;
        Clock.Reset();
    }

    private static void Release()
    {
        if (_pinned.IsAllocated) _pinned.Free();

        Interlocked.Increment(ref _generation);

        var process = _process;
        _process = null;
        try { if (process is { HasExited: false }) process.Kill(); } catch (Exception) { /* already gone */ }
        process?.Dispose();

        if (_tempFile is not null)
        {
            try { File.Delete(_tempFile); } catch (IOException) { /* still open; the OS temp cleaner will get it */ }
            _tempFile = null;
        }
    }
}
