using System;
using System.Runtime.InteropServices;

namespace Re2.Studio;

/// <summary>
/// The small amount of Win32 needed to make the tool behave like a normal desktop application: a real
/// Open dialog, and a console that appears only when one is wanted.
/// </summary>
public static class NativeDialogs
{
    private const int OfnFileMustExist = 0x1000;
    private const int OfnPathMustExist = 0x0800;
    private const int OfnExplorer = 0x00080000;
    private const int OfnNoChangeDir = 0x0008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public string? FileTitle;
        public int MaxFileTitle;
        public string? InitialDirectory;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public string? TemplateName;
        public IntPtr Reserved;
        public int Reserved2;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>Attaches to the launching terminal's console, if there is one.</summary>
    private const int AttachParentProcess = -1;

    /// <summary>
    /// Shows the platform's Open dialog and returns the chosen path, or null if cancelled.
    /// </summary>
    public static string? OpenFile(string title, string filter, string? initialDirectory = null)
    {
        if (OperatingSystem.IsLinux()) return LinuxDesktop.OpenFile(title, filter, initialDirectory);
        if (!OperatingSystem.IsWindows()) return null;

        // The dialog writes the result into this buffer, so it has to be allocated up front.
        const int bufferChars = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufferChars * 2);

        try
        {
            for (int i = 0; i < 4; i++) Marshal.WriteByte(buffer, i, 0);

            var options = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Filter = filter,
                FilterIndex = 1,
                File = buffer,
                MaxFile = bufferChars,
                InitialDirectory = initialDirectory,
                Title = title,
                Flags = OfnFileMustExist | OfnPathMustExist | OfnExplorer | OfnNoChangeDir
            };

            if (!GetOpenFileNameW(ref options)) return null;      // cancelled, or the dialog failed

            string? path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception)
        {
            // A missing comdlg32 or a marshalling problem must not take the application down; the
            // path box is still there.
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reattaches standard output to the launching console, so the headless switches still print when
    /// the tool is run from a terminal.
    /// </summary>
    public static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AttachConsole(AttachParentProcess); } catch (Exception) { /* no console to attach to */ }
    }

    /// <summary>The filter the Open dialog uses for cartridge images.</summary>
    public const string RomFilter = "Nintendo 64 ROMs\0*.z64;*.n64;*.v64\0All files\0*.*\0\0";
}
