using ImGuiNET;

namespace Re2.Studio;

/// <summary>Everything made of words, under one tab.</summary>
public static class TextTab
{
    public const string Files = "Files & memos";
    public const string Names = "Item names";
    public const string Examine = "Item text";

    private static string _requested = "";

    /// <summary>Opens one of the sub-tabs, for captures and for anything linking here.</summary>
    public static void Show(string sub) => _requested = sub;

    public static void Draw(RomSession session)
    {
        if (!ImGui.BeginTabBar("texttabs")) return;

        if (Sub(Files)) { TextPanel.Draw(session); ImGui.EndTabItem(); }
        if (Sub(Names)) { ItemNamePanel.Draw(session); ImGui.EndTabItem(); }
        if (Sub(Examine)) { ItemMessagePanel.Draw(session); ImGui.EndTabItem(); }

        ImGui.EndTabBar();
    }

    private static bool Sub(string label)
    {
        bool wanted = _requested == label;
        if (wanted) _requested = "";

        return TabBar.Begin(label, wanted);
    }
}
