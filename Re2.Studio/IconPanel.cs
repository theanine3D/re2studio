using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Re2.Core.Assets;
using Re2.Core.Project;

namespace Re2.Studio;

/// <summary>
/// The inventory's small item pictures: every one at a glance, and a closer look at whichever is
/// picked, with its item name beside it.
/// </summary>
public static class IconPanel
{
    private const int CacheKey = 3_500_000;
    private const float Thumb = 2f, Zoom = 5f;

    private static int _selected = 1;              // the knife: icon 0 is the blank panel
    private static int _scrollTo = -1;
    private static string _exportFolder = "";
    private static string _exportStatus = "";
    private static string _importPath = "";
    private static string _importStatus = "";
    private static string _error = "";
    private static List<string> _loadedNames = new();
    private static bool _namesLoaded;

    /// <summary>Opens an icon by number, for the link from an item's name.</summary>
    public static void Preselect(int number)
    {
        _selected = number;
        _scrollTo = number;
    }

    /// <summary>Re-reads what depends on the project; the selection is kept.</summary>
    public static void Invalidate() => _namesLoaded = false;

    /// <summary>The item names to label icons with: the Text tab's own, when it has been opened.</summary>
    private static IReadOnlyList<string> Names =>
        ItemNamePanel.Live is { Count: > 0 } live ? live : _loadedNames;

    private static string NameOf(InventoryIcons.Icon icon)
    {
        if (icon.InBundle) return "";
        var names = Names;
        return icon.ItemId < names.Count ? names[icon.ItemId] : "";
    }

    /// <summary>The label a row goes by: the user's own, else the item name, else nothing.</summary>
    private static string Title(InventoryIcons.Icon icon)
    {
        string label = AssetLabels.Get(AssetLabels.Icon, icon.Number);
        return label.Length > 0 ? label : NameOf(icon);
    }

    public static void Draw(RomSession session, TextureCache cache)
    {
        if (!_namesLoaded)
        {
            _loadedNames = ItemNameFile.ExistsIn(ProjectPanel.Folder) &&
                           ItemNameFile.TryRead(ProjectPanel.Folder, out var saved, out _)
                ? saved
                : ItemNames.Read(session.Rom);
            _namesLoaded = true;
        }

        byte[] palette;
        try { palette = AssetIo.IconPalette(session); _error = ""; }
        catch (Exception ex) { _error = ex.Message; palette = Array.Empty<byte>(); }

        if (_error.Length > 0) { ImGui.TextWrapped(_error); return; }

        var icons = InventoryIcons.For(session.Rom.Layout);

        ImGui.Text($"{InventoryIcons.Count} item icons + {InventoryIcons.BundleCount} extras, " +
                   $"{InventoryIcons.Width}x{InventoryIcons.Height}");
        ImGui.TextDisabled($"Drawn with palette {InventoryIcons.PaletteIndex} of menu screen " +
                           $"#{session.Rom.Layout.IconPaletteAsset}, the inventory screen they sit on.");

        string filter = LabelUi.DrawFilter(AssetLabels.Icon);
        var visible = Enumerable.Range(0, icons.Count)
            .Where(i => AssetLabels.Matches(AssetLabels.Icon, i, filter, NameOf(icons[i])))
            .ToList();
        LabelUi.DrawFilterSummary(filter, visible.Count, icons.Count);

        ImGui.Separator();

        // Six to a row at double size, which is the widest the grid can go while leaving the detail
        // pane room for a readable zoom.
        const int Columns = 6;
        var cell = new Vector2(InventoryIcons.Width * Thumb, InventoryIcons.Height * Thumb);
        // Measured from the style rather than guessed: an image button adds frame padding on both
        // sides, and a guess here is what clipped the last column.
        var style = ImGui.GetStyle();
        float gridWidth = Columns * (cell.X + style.FramePadding.X * 2 + style.ItemSpacing.X)
                        + style.WindowPadding.X * 2 + style.ScrollbarSize;

        ImGui.BeginChild("icon-grid", new Vector2(gridWidth, 0), ImGuiChildFlags.Border);

        int column = 0;
        foreach (int i in visible)
        {
            var icon = icons[i];
            var gpu = Texture(session, cache, icon, palette);

            if (column > 0) ImGui.SameLine();
            column = (column + 1) % Columns;

            if (_scrollTo == i) { ImGui.SetScrollHereY(0.5f); _scrollTo = -1; }

            bool selected = i == _selected;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);

            if (ImGui.ImageButton($"icon{i}", (IntPtr)gpu.Handle, cell)) _selected = i;

            if (selected) ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                string title = Title(icon);
                ImGui.SetTooltip(title.Length > 0 ? $"{icon}  -  {title}" : icon.ToString());
            }
        }

        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("icon-detail", Vector2.Zero, ImGuiChildFlags.Border);
        DrawDetail(session, cache, icons[Math.Clamp(_selected, 0, icons.Count - 1)], palette);
        ImGui.EndChild();
    }

    private static void DrawDetail(RomSession session, TextureCache cache, InventoryIcons.Icon icon,
                                   byte[] palette)
    {
        string name = NameOf(icon);

        ImGui.Text(icon.InBundle
            ? $"{icon}   in asset #{icon.AssetId}, picture {icon.Number - InventoryIcons.Count + 1} of {InventoryIcons.BundleCount}"
            : $"{icon}   asset #{icon.AssetId}");

        if (name.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{name}##toname"))
            {
                ItemNamePanel.Preselect(icon.ItemId);
                TextTab.Show(TextTab.Names);
                Program.ShowTab("Text");
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show this item's name and description on the Text tab.");
        }
        else if (icon.InBundle)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("an extra picture, not tied to one item id");
        }

        LabelUi.DrawEditor(AssetLabels.Icon, icon.Number);

        var gpu = Texture(session, cache, icon, palette);
        ImGui.Image((IntPtr)gpu.Handle, new Vector2(InventoryIcons.Width * Zoom, InventoryIcons.Height * Zoom));

        ImGui.Separator();
        AssetIoUi.DrawExportFolder(session, ref _exportFolder, "icons");

        if (ImGui.Button($"Export {icon} as PNG"))
            _exportStatus = PanelIo.Run(() => "wrote " + AssetIo.ExportIconPng(session, icon, _exportFolder));

        ImGui.SameLine();
        if (ImGui.Button($"Export all {InventoryIcons.For(session.Rom.Layout).Count}"))
            _exportStatus = PanelIo.Run(() => AssetIo.ExportIcons(session, _exportFolder));

        // The palette every icon is drawn with, which lives on the inventory screen rather than in any
        // icon.
        ImGui.SameLine();
        if (ImGui.Button("Export palette"))
            _exportStatus = PanelIo.Run(() => "wrote " + AssetIo.ExportMenuPalettePng(
                session, session.Rom.Layout.IconPaletteAsset, _exportFolder, InventoryIcons.PaletteIndex));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Palette {InventoryIcons.PaletteIndex} of menu screen #{session.Rom.Layout.IconPaletteAsset}, " +
                             "as a 256x1 strip: entry 0 is the leftmost pixel.");

        AssetIoUi.DrawStatus(_exportStatus);

        ImGui.Separator();
        bool ready = AssetIoUi.DrawProjectNote();

        ImGui.SetNextItemWidth(360);
        ImGui.InputText("PNG to import", ref _importPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose...##icon"))
        {
            string? picked = NativeDialogs.OpenFile("Choose a replacement icon",
                                                    "PNG images\0*.png\0All files\0*.*\0");
            if (picked is not null) _importPath = picked;
        }

        ImGui.BeginDisabled(!ready || _importPath.Length == 0);
        if (ImGui.Button($"Import into {icon}"))
            _importStatus = PanelIo.Run(() => AssetIo.ImportIcon(session, ProjectPanel.Folder, icon, _importPath));
        ImGui.EndDisabled();

        AssetIoUi.DisabledWrapped(
            $"The replacement must be exactly {InventoryIcons.Width}x{InventoryIcons.Height}. Its colours are " +
            $"matched into palette {InventoryIcons.PaletteIndex} of the inventory screen, which is never " +
            "changed here: the whole screen is drawn with it, so a new palette would recolour everything " +
            "around the icons. A colour the palette lacks becomes the nearest one it has. Exporting an " +
            "icon first and drawing over it keeps you inside the palette.");
        AssetIoUi.DrawStatus(_importStatus);
    }

    /// <summary>An icon on the GPU.</summary>
    private static GlTexture Texture(RomSession session, TextureCache cache, InventoryIcons.Icon icon, byte[] palette)
        => cache.Get(CacheKey + icon.Number, () =>
            (AssetIo.IconRgba(session, icon, palette) ?? new byte[InventoryIcons.Size * 4],
             InventoryIcons.Width, InventoryIcons.Height));
}
