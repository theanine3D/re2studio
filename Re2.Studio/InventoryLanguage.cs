using ImGuiNET;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Studio;

/// <summary>
/// Which language the item name and item text tabs edit. Europe and Japan have a choice: each
/// carries the English text and a second language beside it -- French and Japanese.
/// </summary>
public static class InventoryLanguage
{
    private static bool _alternate;
    private static Re2Layout _layout = Re2Layout.UsaRev1;

    /// <summary>Chooses the language up front, for captures run from the command line.</summary>
    public static void Prefer(bool alternate) => _preferred = alternate;

    private static bool? _preferred;

    /// <summary>True when the build's second language is the one being edited.</summary>
    public static bool Alternate => _alternate && _layout.AlternateInventoryText is not null;

    public static ItemCharset Charset => ItemNames.CharsetOf(_layout, Alternate);

    /// <summary>The project-file suffix for the chosen language, or null for English.</summary>
    public static string? FileSuffix => Alternate ? _layout.AlternateInventoryText!.FileSuffix : null;

    public static int NamesCapacity => ItemNames.CapacityFor(_layout, Alternate);
    public static int MessagesCapacity => ItemMessages.CapacityFor(_layout, Alternate);

    /// <summary>The characters the chosen language can be typed in, for the panels' legends.</summary>
    public static string Characters => Charset switch
    {
        ItemCharset.French => "A-Z, a-z, 0-9, space, . , ! ? : / ' - \" & « °, and à è é ê ë ô ù ç Ç",
        ItemCharset.Japanese => "hiragana, katakana, A-Z, a-z, 0-9, the punctuation 。、「」『』()・ー!?, " +
                                "and the kanji in the game's two font sheets (menu assets 5410 and 5411)",
        _ => "A-Z, a-z, 0-9, space, and . , ! ? / ' -"
    };

    /// <summary>
    /// Follows the open ROM and, when it has a second language, draws the picker. Switching drops
    /// both panels' loaded text so they read the other language; it is refused while either holds
    /// unsaved edits, which would otherwise be lost.
    /// </summary>
    public static void Draw(RomSession session)
    {
        var layout = session.Rom.Layout;
        if (!ReferenceEquals(layout, _layout))
        {
            // Each build opens on the language its players read: English in Europe, Japanese in Japan.
            _layout = layout;
            _alternate = _preferred ?? layout == Re2Layout.Japan;
            Reload();
        }

        if (layout.AlternateInventoryText is not { } alt) return;

        bool dirty = ItemNamePanel.HasUnsavedEdits || ItemMessagePanel.HasUnsavedEdits;

        ImGui.BeginDisabled(dirty);
        int choice = _alternate ? 1 : 0;
        ImGui.SetNextItemWidth(140);
        if (ImGui.Combo("language", ref choice, $"English\0{alt.Language}\0") && (choice == 1) != _alternate)
        {
            _alternate = choice == 1;
            Reload();
        }
        ImGui.EndDisabled();

        if (dirty && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Save or revert the edits first; switching language would lose them.");
    }

    private static void Reload()
    {
        ItemNamePanel.Invalidate();
        ItemMessagePanel.Invalidate();
    }
}
