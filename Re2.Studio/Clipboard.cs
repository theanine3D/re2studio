using System;
using System.Runtime.InteropServices;

namespace Re2.Studio;

/// <summary>Putting an image on the clipboard.</summary>
public static class Clipboard
{
    /// <summary>Copies an image at exactly the size given.</summary>
    public static string SetImage(byte[] rgba, int width, int height)
    {
        if (OperatingSystem.IsLinux())
        {
            LinuxDesktop.CopyPng(Re2.Core.Assets.ImageCodec.RgbaToPng(rgba, width, height));
            return $"copied {width}x{height} to the clipboard";
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Copying an image to the clipboard is not supported here.");

        Put(BuildDib(rgba, width, height));
        return $"copied {width}x{height} to the clipboard";
    }

    /// <summary>The CF_DIB bytes for an image.</summary>
    public static byte[] BuildDib(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("The image has no size.");
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4:N0} bytes of RGBA, got {rgba.Length:N0}.");

        // Rows are padded to a 4-byte boundary, which for 24-bit pixels is not automatic.
        int stride = (width * 3 + 3) & ~3;
        int pixels = stride * height;

        var dib = new byte[HeaderSize + pixels];

        // BITMAPINFOHEADER.
        BitConverter.GetBytes(HeaderSize).CopyTo(dib, 0);      // biSize
        BitConverter.GetBytes(width).CopyTo(dib, 4);           // biWidth
        BitConverter.GetBytes(height).CopyTo(dib, 8);          // biHeight
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);       // biPlanes
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);      // biBitCount
        BitConverter.GetBytes(0).CopyTo(dib, 16);              // biCompression = BI_RGB
        BitConverter.GetBytes(pixels).CopyTo(dib, 20);         // biSizeImage

        for (int y = 0; y < height; y++)
        {
            int from = y * width * 4;
            int to = HeaderSize + (height - 1 - y) * stride;   // bottom-up

            for (int x = 0; x < width; x++)
            {
                dib[to + x * 3] = rgba[from + x * 4 + 2];      // B
                dib[to + x * 3 + 1] = rgba[from + x * 4 + 1];  // G
                dib[to + x * 3 + 2] = rgba[from + x * 4];      // R
            }
        }

        return dib;
    }

    /// <summary>
    /// Hands the bytes to the clipboard, which takes ownership of the memory on success.
    /// </summary>
    private static void Put(byte[] dib)
    {
        if (!OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("Another application is holding the clipboard open.");

        IntPtr handle = IntPtr.Zero;

        try
        {
            EmptyClipboard();

            handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)dib.Length);
            if (handle == IntPtr.Zero) throw new OutOfMemoryException("Could not allocate clipboard memory.");

            IntPtr target = GlobalLock(handle);
            if (target == IntPtr.Zero) throw new InvalidOperationException("Could not lock clipboard memory.");

            try { Marshal.Copy(dib, 0, target, dib.Length); }
            finally { GlobalUnlock(handle); }

            if (SetClipboardData(CF_DIB, handle) == IntPtr.Zero)
                throw new InvalidOperationException("The clipboard refused the image.");

            handle = IntPtr.Zero;   // the clipboard owns it now
        }
        finally
        {
            if (handle != IntPtr.Zero) GlobalFree(handle);
            CloseClipboard();
        }
    }

    private const int HeaderSize = 40;
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr handle);
}
