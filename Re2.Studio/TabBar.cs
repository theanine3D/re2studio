using System;
using System.Text;
using ImGuiNET;

namespace Re2.Studio;

/// <summary>Opening a tab without ImGui drawing a close button on it.</summary>
public static class TabBar
{
    /// <summary>Begins a tab item, optionally selecting it.</summary>
    public static unsafe bool Begin(string label, bool selected)
    {
        var flags = selected ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

        int length = Encoding.UTF8.GetByteCount(label);
        Span<byte> utf8 = stackalloc byte[length + 1];
        Encoding.UTF8.GetBytes(label, utf8);
        utf8[length] = 0;

        fixed (byte* text = utf8)
            return ImGuiNative.igBeginTabItem(text, null, flags) != 0;
    }
}
