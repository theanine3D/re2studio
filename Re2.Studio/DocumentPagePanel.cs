using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Re2.Core.Formats;

namespace Re2.Studio;

/// <summary>
/// Japan's files and memos. Unlike the other builds', they are not text: every page is a picture of
/// text, so they are edited by exporting a page as a PNG, drawing on it and importing it back.
/// </summary>
public static class DocumentPagePanel
{
    public static void Invalidate() => _pages = null;

    private const int CacheKey = 0x7300000;
    private const int Zoom = 2;

    private static List<int>? _pages;
    private static readonly Selection _selection = new();
    private static string _filter = "";
    private static string _exportFolder = "";
    private static string _exportStatus = "";
    private static string _importPath = "";
    private static string _importStatus = "";

    public static void Draw(RomSession session, TextureCache cache)
    {
        if (_pages is null)
        {
            // Every id in the range that decodes: the list of pages the game's file viewer can show.
            var range = session.Rom.Layout.DocumentPages!.Value;
            _pages = session.Assets.Entries
                .Select(e => e.Index)
                .Where(id => id >= range.First && id <= range.Last)
                .Distinct()
                .OrderBy(id => id)
                .Where(id => AssetIo.DocumentPage(session, id) is not null)
                .ToList();

            // Pages are drawn from what the project holds, so an import shows straight away.
            for (int i = 0; i < 0x200; i++) cache.Forget(CacheKey + i);
        }

        var pages = _pages;
        if (pages.Count == 0)
        {
            ImGui.TextDisabled("No document pages decoded.");
            return;
        }

        ImGui.TextWrapped($"{pages.Count} document pages. Biohazard 2's files and memos are pictures of " +
                          "text, not strings: each page is a 256-pixel-wide image in four grey levels. " +
                          "Export a page, draw on it, and import it back.");

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("filter", ref _filter, 64);

        var visible = pages.Select((id, i) => (id, i))
                           .Where(p => _filter.Length == 0 || p.id.ToString().Contains(_filter) ||
                                       AssetLabels.Matches(AssetLabels.Text, p.id, _filter))
                           .Select(p => p.i).ToList();

        _selection.HandleArrowKeys(visible);
        if (_selection.Count == 0) _selection.Set(0);

        ImGui.BeginChild("pages", new Vector2(220, 0), ImGuiChildFlags.Border);
        foreach (int i in visible)
        {
            int id = pages[i];
            string label = AssetLabels.Get(AssetLabels.Text, id);
            if (ImGui.Selectable($"page {id}{(label.Length > 0 ? "  " + label : "")}##p{id}", _selection.Contains(i)))
                _selection.Click(i, visible);
            if (_selection.ScrollToRow == i) { ImGui.SetScrollHereY(0.5f); _selection.ScrollToRow = -1; }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("page-detail", Vector2.Zero, ImGuiChildFlags.Border);

        int current = Math.Clamp(_selection.Primary, 0, pages.Count - 1);
        int assetId = pages[current];
        var page = AssetIo.DocumentPage(session, assetId);

        if (page is not null)
        {
            ImGui.Text($"page {assetId}   {page.Width}x{page.Height}, 4 levels");
            LabelUi.DrawEditor(AssetLabels.Text, assetId);

            var gpu = cache.Get(CacheKey + (assetId & 0x1FF), () => (AssetIo.DocumentRgba(page), page.Width, page.Height));
            ImGui.Image((IntPtr)gpu.Handle, new Vector2(page.Width * Zoom, page.Height * Zoom));
        }

        ImGui.Separator();
        AssetIoUi.DrawExportFolder(session, ref _exportFolder, "documents");

        if (ImGui.Button($"Export page {assetId} as PNG"))
            _exportStatus = PanelIo.Run(() => "wrote " + AssetIo.ExportDocumentPng(session, assetId, _exportFolder));

        var chosen = _selection.Indices.Where(i => i >= 0 && i < pages.Count).Select(i => pages[i]).ToList();
        if (chosen.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Export {chosen.Count} selected"))
                _exportStatus = PanelIo.Run(() => AssetIo.ExportDocuments(session, chosen, _exportFolder));
        }

        ImGui.SameLine();
        if (ImGui.Button($"Export all {pages.Count}"))
            _exportStatus = PanelIo.Run(() => AssetIo.ExportDocuments(session, pages, _exportFolder));

        AssetIoUi.DrawStatus(_exportStatus);

        ImGui.Separator();
        bool ready = AssetIoUi.DrawProjectNote();

        ImGui.SetNextItemWidth(360);
        ImGui.InputText("PNG to import", ref _importPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose...##page"))
        {
            string? picked = NativeDialogs.OpenFile("Choose a replacement page", "PNG images\0*.png\0All files\0*.*\0");
            if (picked is not null) _importPath = picked;
        }

        ImGui.BeginDisabled(!ready || _importPath.Length == 0);
        if (ImGui.Button($"Import into page {assetId}"))
        {
            _importStatus = PanelIo.Run(() => AssetIo.ImportDocumentPng(session, ProjectPanel.Folder, assetId, _importPath));
            cache.Forget(CacheKey + (assetId & 0x1FF));
        }
        ImGui.EndDisabled();

        AssetIoUi.DisabledWrapped(
            "The replacement must be the page's own size. Each pixel's brightness is rounded to the " +
            "nearest of the page's four levels -- black, dark grey, light grey and white -- so drawing " +
            "in exactly those four keeps what you see. The game draws the text through a palette of its " +
            "own, so the greys here stand for its ink shades rather than showing their exact colour.");
        AssetIoUi.DrawStatus(_importStatus);

        ImGui.EndChild();
    }
}
