using ImGuiNET;

namespace Re2.Studio;

/// <summary>
/// The menu artwork, under one tab: the whole screens, and the small item icons drawn on top of the
/// inventory one.
/// </summary>
public static class MenusTab
{
    public const string Screens = "Screens";
    public const string Icons = "Icons";

    private static string _requested = "";

    /// <summary>Opens one of the sub-tabs, for captures and for anything linking here.</summary>
    public static void Show(string sub) => _requested = sub;

    public static void Draw(RomSession session, TextureCache cache)
    {
        if (!ImGui.BeginTabBar("menutabs")) return;

        if (Sub(Screens)) { MenuPanel.Draw(session, cache); ImGui.EndTabItem(); }
        if (Sub(Icons)) { IconPanel.Draw(session, cache); ImGui.EndTabItem(); }

        ImGui.EndTabBar();
    }

    private static bool Sub(string label)
    {
        bool wanted = _requested == label;
        if (wanted) _requested = "";

        return TabBar.Begin(label, wanted);
    }
}
