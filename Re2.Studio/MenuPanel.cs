using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Re2.Core.Formats;

namespace Re2.Studio;

/// <summary>
/// The menu screens -- inventory, map, file viewer -- which the game stores in their own container
/// rather than the one the Textures tab reads.
/// </summary>
public static class MenuPanel
{
    private static readonly Selection _selection = new();

    private static int _selected;
    private static int _palette = -1;
    private static string _exportFolder = "";
    private static string _exportStatus = "";
    private static string _importPath = "";
    private static string _importStatus = "";

    private const int CacheKey = 3_000_000;

    private static string _palettePath = "";
    private static string _paletteStatus = "";

    /// <summary>
    /// Drops the decoded copies after an edit, keeping the screen the user is on -- an import
    /// invalidates, and being thrown back to the top of the list every time you change a palette
    /// would make the thing unusable.
    /// </summary>
    public static void Invalidate()
    {
        // Only restore what was open; never overwrite a request someone else already made.
        if (Pending < 0 && _lastAssetId >= 0) Pending = _lastAssetId;
    }

    private static int _lastAssetId = -1;

    /// <summary>Opens a given asset, for captures and for anything that links here.</summary>
    public static int Pending { get; set; } = -1;

    public static void Draw(RomSession session, TextureCache cache)
    {
        var list = session.MenuImages;

        if (list.Count == 0)
        {
            ImGui.TextWrapped("This ROM has no menu screens.");
            return;
        }

        if (Pending >= 0)
        {
            int found = -1;
            for (int i = 0; i < list.Count; i++) if (list[i].Entry.Index == Pending) found = i;
            if (found >= 0)
            {
                _selected = found;
                _selection.Set(found);
                _selection.ScrollToRow = found;
                LabelUi.ClearFilter(AssetLabels.Menu);
            }
            Pending = -1;
        }

        ImGui.Text($"{list.Count} menu screens");
        ImGui.TextDisabled("Whole screens, not pieces: the inventory portraits are the row along the " +
                           "bottom of the status screens, and the game draws one by picking a rectangle.");
        ImGui.Separator();

        string filter = LabelUi.DrawFilter(AssetLabels.Menu);

        var visible = Enumerable.Range(0, list.Count)
            .Where(i => AssetLabels.Matches(AssetLabels.Menu, list[i].Entry.Index, filter))
            .ToList();

        LabelUi.DrawFilterSummary(filter, visible.Count, list.Count);

        if (_selection.Count == 0) _selection.Set(_selected);

        // Arrow keys move the selection, the same as every other list in the editor.
        if (_selection.HandleArrowKeys(visible))
        {
            _selected = _selection.Primary;
            _palette = -1;
        }

        ImGui.BeginChild("menu-list", new Vector2(240, 0), ImGuiChildFlags.Border);

        foreach (int i in visible)
        {
            var (entry, _) = list[i];

            // Id and label only.
            string caption = LabelUi.Caption(AssetLabels.Menu, entry.Index, $"#{entry.Index}");

            if (_selection.ScrollToRow >= 0 && i == _selection.Primary)
            {
                ImGui.SetScrollHereY(0.5f);
                _selection.ScrollToRow = -1;
            }

            // Set rather than Click: this panel acts on one screen at a time, so a ctrl-click
            // multi-selection would highlight rows the preview and the buttons cannot follow.
            if (ImGui.Selectable($"{caption}##menu{i}", _selection.Contains(i)))
            {
                _selection.Set(i);
                _selected = i;
                _palette = -1;
            }
        }

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("menu-view", Vector2.Zero, ImGuiChildFlags.Border);

        _selected = Math.Clamp(_selected, 0, list.Count - 1);
        var (current, menu) = list[_selected];
        _lastAssetId = current.Index;

        ImGui.Text($"asset #{current.Index}   {menu.Width}x{menu.Height}   " +
                   $"{current.StoredSize:N0} bytes stored   {menu.Palettes.Count} palette(s)");

        LabelUi.DrawEditor(AssetLabels.Menu, current.Index);

        // A screen with several palettes uses them per region, not one for the whole image -- the
        // portraits read correctly under one and the panel behind them under another.
        int shown = _palette < 0 ? menu.DisplayPalette : _palette;

        if (menu.Palettes.Count > 1)
        {
            for (int p = 0; p < menu.Palettes.Count; p++)
            {
                if (p > 0) ImGui.SameLine();
                string label = p == menu.DisplayPalette ? $"palette {p} (portraits)" : $"palette {p}";
                if (ImGui.RadioButton($"{label}##pal{p}", shown == p)) _palette = p;
            }
        }

        var gpu = cache.Get(CacheKey + current.Index * 16 + shown,
                            () => (menu.ToRgba(shown), menu.Width, menu.Height));

        float scale = menu.Width <= 128 ? 3f : 2f;
        ImGui.Image((IntPtr)gpu.Handle, new Vector2(menu.Width * scale, menu.Height * scale));

        ImGui.SameLine();
        DrawPaletteEditor(session, menu, current.Index, shown);

        ImGui.Separator();
        AssetIoUi.DrawExportFolder(session, ref _exportFolder, "menus");

        if (ImGui.Button($"Export #{current.Index} as PNG"))
            _exportStatus = PanelIo.Run(() =>
                "wrote " + AssetIo.ExportMenuImagePng(session, current.Index, _exportFolder, shown));

        ImGui.SameLine();
        if (ImGui.Button($"Export all {list.Count}"))
            _exportStatus = PanelIo.Run(() =>
                AssetIo.ExportMenuImages(session, list.Select(m => m.Entry.Index), _exportFolder, shown));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Uses palette {shown} throughout, falling back for screens that have fewer.");

        AssetIoUi.DrawStatus(_exportStatus);

        ImGui.Separator();
        bool ready = AssetIoUi.DrawProjectNote();

        ImGui.SetNextItemWidth(420);
        ImGui.InputText("PNG to import", ref _importPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose...##menu"))
        {
            string? picked = NativeDialogs.OpenFile("Choose a replacement screen",
                                                    "PNG images\0*.png\0All files\0*.*\0");
            if (picked is not null) _importPath = picked;
        }

        ImGui.BeginDisabled(!ready || _importPath.Length == 0);
        if (ImGui.Button($"Import into #{current.Index}"))
            _importStatus = PanelIo.Run(() =>
                AssetIo.ImportMenuImage(session, ProjectPanel.Folder, current.Index, _importPath, shown));
        ImGui.EndDisabled();

        // Left enabled with an empty path, so pressing it says what is missing.
        ImGui.SameLine();
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("Import with new palette"))
            _importStatus = PanelIo.Run(() =>
            {
                var (log, adjusted) = AssetIo.ImportMenuImageWithNewPalette(
                    session, ProjectPanel.Folder, current.Index, _importPath, shown);
                _importPath = adjusted;
                return log;
            });
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"Builds a new palette {shown} from the picture (merging similar colours past " +
                             "256), matches the picture into it, saves both beside the PNG and imports them.");

        AssetIoUi.DisabledWrapped("The replacement must match the dimensions exactly. Its colours are matched " +
                        $"into palette {shown} -- the one shown above -- so export and import agree " +
                        "with what is on screen. New colours become the nearest one already there; " +
                        "to bring in colours the screen does not have, use Import with new palette.");
        AssetIoUi.DrawStatus(_importStatus);

        ImGui.EndChild();
    }


    /// <summary>The palette beside the picture: what the colours are, and how to change them.</summary>
    private static void DrawPaletteEditor(RomSession session, MenuImage menu, int assetId, int palette)
    {
        ImGui.BeginGroup();

        // The notes here are prose, and the group sits against the right edge of the window, so it
        // needs its own wrap width or the ends of the sentences fall off the screen.
        const float Width = 320f;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Width);

        ImGui.TextUnformatted($"Palette {palette}");
        ImGui.TextDisabled($"{menu.Colours} colours, in the order the game reads them");
        ImGui.Spacing();

        // The whole palette at a glance, entry 0 top-left, reading along each row.
        var colours = menu.PaletteToRgba(palette);
        const int PerRow = 16;
        var swatch = new Vector2(14, 14);

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(1, 1));

        for (int i = 0; i < menu.Colours; i++)
        {
            if (i % PerRow != 0) ImGui.SameLine();

            var colour = new Vector4(colours[i * 4] / 255f, colours[i * 4 + 1] / 255f,
                                     colours[i * 4 + 2] / 255f, colours[i * 4 + 3] / 255f);

            // AlphaPreview rather than a flat swatch: a transparent entry and a black one are
            // different things here, and the format keeps only one bit to tell them apart.
            ImGui.ColorButton($"##swatch{i}", colour,
                              ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.AlphaPreview, swatch);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"entry {i}   " +
                                 $"{colours[i * 4]}, {colours[i * 4 + 1]}, {colours[i * 4 + 2]}   " +
                                 (colours[i * 4 + 3] >= 128 ? "opaque" : "transparent"));
        }

        ImGui.PopStyleVar();

        ImGui.Spacing();

        if (ImGui.Button($"Export palette {palette} as PNG"))
            _paletteStatus = PanelIo.Run(() =>
                "wrote " + AssetIo.ExportMenuPalettePng(session, assetId, _exportFolder, palette));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"A {menu.Colours}x1 strip: entry 0 is the leftmost pixel.");

        bool ready = AssetIo.HasProject(ProjectPanel.Folder);

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("palette PNG", ref _palettePath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose...##pal"))
        {
            string? picked = NativeDialogs.OpenFile("Choose a palette strip",
                                                    "PNG images\0*.png\0All files\0*.*\0");
            if (picked is not null) _palettePath = picked;
        }

        ImGui.BeginDisabled(!ready || _palettePath.Length == 0);
        if (ImGui.Button($"Replace palette {palette}"))
            _paletteStatus = PanelIo.Run(() =>
                AssetIo.ImportMenuPalette(session, ProjectPanel.Folder, assetId, _palettePath, palette));
        ImGui.EndDisabled();

        ImGui.TextDisabled($"Must be {menu.Colours}x1. The picture keeps its indices, so this changes " +
                           "colours everywhere they are used in this screen.");

        AssetIoUi.DrawStatus(_paletteStatus);

        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
    }
}
