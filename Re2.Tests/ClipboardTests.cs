using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>The bytes handed to the Windows clipboard.</summary>
public class ClipboardTests
{
    [Fact]
    public void TheDibIsBottomUpBgrAtTheExactSize()
    {
        // Two rows of two: top row red then green, bottom row blue then white.
        var rgba = new byte[]
        {
            255, 0, 0, 255,    0, 255, 0, 255,
            0, 0, 255, 255,    255, 255, 255, 255,
        };

        var dib = Clipboard.BuildDib(rgba, 2, 2);

        // BITMAPINFOHEADER: 40 bytes, then rows padded to 4 bytes -- 2 pixels of 3 bytes is 6,
        // which pads to 8.
        Assert.Equal(40 + 8 * 2, dib.Length);
        Assert.Equal(40, System.BitConverter.ToInt32(dib, 0));
        Assert.Equal(2, System.BitConverter.ToInt32(dib, 4));    // width
        Assert.Equal(2, System.BitConverter.ToInt32(dib, 8));    // height, positive = bottom-up
        Assert.Equal(24, System.BitConverter.ToInt16(dib, 14));  // bits per pixel
        Assert.Equal(0, System.BitConverter.ToInt32(dib, 16));   // BI_RGB

        // The first row stored is the LAST row of the image, and each pixel is BGR.
        Assert.Equal(new byte[] { 255, 0, 0 }, dib[40..43]);       // blue
        Assert.Equal(new byte[] { 255, 255, 255 }, dib[43..46]);   // white

        Assert.Equal(new byte[] { 0, 0, 255 }, dib[48..51]);       // red
        Assert.Equal(new byte[] { 0, 255, 0 }, dib[51..54]);       // green
    }

    /// <summary>A background is 320x224, and the point of the button is that it copies exactly that.</summary>
    [Fact]
    public void ABackgroundSizedImageKeepsItsDimensions()
    {
        var dib = Clipboard.BuildDib(new byte[320 * 224 * 4], 320, 224);

        Assert.Equal(320, System.BitConverter.ToInt32(dib, 4));
        Assert.Equal(224, System.BitConverter.ToInt32(dib, 8));
        Assert.Equal(40 + 320 * 3 * 224, dib.Length);
    }
}
