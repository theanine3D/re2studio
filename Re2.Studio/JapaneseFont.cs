using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ImGuiNET;
using Re2.Core.Formats;

namespace Re2.Studio;

/// <summary>
/// Gives the UI font the Japanese characters Biohazard 2's text uses. ImGui's built-in font is
/// ASCII only, so a system font with kana and kanji is merged in -- restricted to the characters
/// the game's own font sheets hold, which keeps the atlas small.
/// </summary>
public static class JapaneseFont
{
    private static readonly string[] Candidates =
    {
        // Windows
        @"C:\Windows\Fonts\msgothic.ttc",
        @"C:\Windows\Fonts\meiryo.ttc",
        @"C:\Windows\Fonts\YuGothM.ttc",
        // Linux (Noto CJK and IPA, as the common distributions package them)
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/google-noto-cjk/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/truetype/fonts-japanese-gothic.ttf",
        "/usr/share/fonts/opentype/ipafont-gothic/ipag.ttf",
    };

    /// <summary>The font merged in, or null when none was found (Japanese then draws as '?').</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>Call from the controller's IO setup, before the font atlas is built.</summary>
    public static unsafe void Merge()
    {
        string? path = Array.Find(Candidates, File.Exists);
        if (path is null) return;

        var io = ImGui.GetIO();
        io.Fonts.AddFontDefault();

        // ImGui keeps the range pointer until the atlas is rebuilt, so it lives for the process.
        var ranges = BuildRanges();
        IntPtr block = Marshal.AllocHGlobal(ranges.Length * sizeof(ushort));
        for (int i = 0; i < ranges.Length; i++) Marshal.WriteInt16(block, i * 2, (short)ranges[i]);

        var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        config.MergeMode = true;
        config.PixelSnapH = true;

        io.Fonts.AddFontFromFileTTF(path, 13f, config, block);
        LoadedFrom = path;
    }

    /// <summary>Pairs of first/last code points, zero-terminated, as ImGui wants them.</summary>
    private static ushort[] BuildRanges()
    {
        var points = new SortedSet<ushort>(ItemText.JapaneseGlyphs.Where(c => c > 0x7F).Select(c => (ushort)c));

        // All kana too, so anything typed rather than read from the ROM still shows.
        for (int c = 0x3000; c <= 0x30FF; c++) points.Add((ushort)c);

        var ranges = new List<ushort>();
        foreach (ushort p in points)
        {
            if (ranges.Count > 0 && ranges[^1] == p - 1) ranges[^1] = p;
            else { ranges.Add(p); ranges.Add(p); }
        }

        ranges.Add(0);
        return ranges.ToArray();
    }
}
