using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using ImGuiNET;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Patch;
using Re2.Core.Project;
using Re2.Core.Rom;

namespace Re2.Studio;

/// <summary>Gallery of the prerendered backgrounds, with in-place replacement.</summary>
public static class BackgroundPanel
{
    private static readonly Selection _selection = new();

    private static string _exportFolder = "";

    // Each reported under its own buttons.
    private static string _exportStatus = "";
    private static string _importStatus = "";

    /// <summary>Seeds a multi-selection for a headless capture, which cannot Shift+click.</summary>
    public static void PreselectRange(int start, int count) => _pendingRange = (start, count);

    /// <summary>Selects one background and scrolls it into view.</summary>
    public static void Preselect(int index)
    {
        LabelUi.ClearFilter(AssetLabels.Background);
        _pendingIndex = index;
    }

    private static int _pendingIndex = -1;

    private static (int Start, int Count)? _pendingRange;
    private static int _selected;

    /// <summary>Whether the preview picks out the foreground.</summary>
    private static bool _showForegrounds
    {
        get => RoomPanel.ShowForegrounds;
        set => RoomPanel.ShowForegrounds = value;
    }

    /// <summary>The checkbox, with a word on what the picture is showing.</summary>
    private static void DrawForegroundToggle(MaskFile? mask)
    {
        bool show = _showForegrounds;
        if (ImGui.Checkbox("Show foreground", ref show)) _showForegrounds = show;

        ImGui.SameLine();

        if (mask is null)
        {
            ImGui.TextDisabled("no foreground on this view");
        }
        else
        {
            int own = mask.Pieces.Count(p => p.HasOwnPixels);
            ImGui.TextDisabled($"{mask.Pieces.Count} pieces, {mask.CoveredPixels:N0} pixels" +
                               (own > 0 ? $", {own} with their own image" : ""));
        }

        if (ImGui.IsItemHovered() || ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Parts of the background that draw in front of the player. They are tinted, " +
                             "not composited: they are copied from the background itself, so drawing " +
                             "them faithfully would change nothing on screen.");
    }
    private static string _importPath = "";

    /// <summary>Kept apart from the background's path on purpose -- see the import section.</summary>
    private static string _foregroundPath = "";

    public static void Draw(RomSession session, TextureCache cache, ref string status)
    {
        var list = session.Backgrounds.Backgrounds;
        if (list.Count == 0) { ImGui.Text("No backgrounds found."); return; }

        string filter = LabelUi.DrawFilter(AssetLabels.Background);

        _selection.Constrain(list.Count);
        if (_pendingRange is { } range)
        {
            _selection.SetRange(range.Start, range.Count, list.Count);
            _pendingRange = null;
        }

        if (_pendingIndex >= 0 && _pendingIndex < list.Count)
        {
            _selection.Set(_pendingIndex);
            _selection.ScrollToRow = _pendingIndex;
            _pendingIndex = -1;
        }

        // The visible order is needed before the rows are drawn, so a Shift range can be measured
        // over what is actually on screen rather than the unfiltered list.
        var visible = new List<int>(list.Count);
        for (int i = 0; i < list.Count; i++)
            if (AssetLabels.Matches(AssetLabels.Background, list[i].Index, filter))
                visible.Add(i);

        _selection.HandleArrowKeys(visible);

        ImGui.BeginChild("bg-list", new Vector2(300, 0), ImGuiChildFlags.Border);
        foreach (int i in visible)
        {
            var bg = list[i];
            string caption = LabelUi.Caption(AssetLabels.Background, bg.Index, $"bg{bg.Index:D4}");
            if (ImGui.Selectable($"{caption}##{i}", _selection.Contains(i)))
                _selection.Click(i, visible);

            // A keyboard move can land on a row that is scrolled out of sight.
            if (_selection.ScrollToRow >= 0 && i == _selection.Primary)
            {
                ImGui.SetScrollHereY(0.5f);
                _selection.ScrollToRow = -1;
            }
        }
        ImGui.EndChild();
        LabelUi.DrawFilterSummary(filter, visible.Count, list.Count);

        if (_selection.Count == 0 && visible.Count > 0) _selection.Set(visible[0]);
        _selected = Math.Max(0, _selection.Primary);

        ImGui.SameLine();
        ImGui.BeginChild("bg-view", Vector2.Zero, ImGuiChildFlags.Border);

        var current = list[_selected];
        var mask = session.MaskForBackground(current.Index);

        // Tinted and plain are different pictures, so they cannot share a cache key -- one would keep
        // showing after the checkbox changed.
        var texture = cache.Get((_showForegrounds && mask is not null ? 0x1100000 : 0x1000000) | current.Index, () =>
        {
            var (pixels, w, h) = BackgroundCodec.JpegToRgba(session.GetBackgroundJpeg(current.Index));

            if (!_showForegrounds || mask is null) return (pixels, w, h);

            return (MaskRenderer.Highlight(mask, pixels, w, h), mask.Width, mask.Height);
        });

        ImGui.Text($"background {current.Index}   {current.Width}x{current.Height}   " +
                   $"{current.Length:N0} bytes stored, slot holds {current.SlotCapacity:N0}");
        LabelUi.DrawEditor(AssetLabels.Background, _selection.ToAssetIds(i => list[i].Index));

        DrawForegroundToggle(mask);

        ImGui.Image((IntPtr)texture.Handle, new Vector2(texture.Width * 2, texture.Height * 2));
        LabelUi.DrawSelectionBadge(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                                   _selection.Count, $"bg{current.Index:D4}");

        ImGui.Separator();

        AssetIoUi.DrawExportFolder(session, ref _exportFolder, "backgrounds");

        if (ImGui.Button("Copy to Clipboard"))
            _exportStatus = PanelIo.Run(() =>
            {
                var (pixels, w, h) = BackgroundCodec.JpegToRgba(session.GetBackgroundJpeg(current.Index));

                // The picture as it is, not as it is shown: the preview draws at double size.
                return Clipboard.SetImage(pixels, w, h);
            });

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Copies bg{current.Index:D4} at its own {current.Width}x{current.Height}.");

        ImGui.SameLine();
        if (ImGui.Button($"Export bg{current.Index:D4} as PNG"))
            _exportStatus = PanelIo.Run(() =>
            {
                Directory.CreateDirectory(_exportFolder);
                string file = Path.Combine(_exportFolder, $"bg{current.Index:D4}.png");
                File.WriteAllBytes(file, BackgroundCodec.JpegToPng(session.GetBackgroundJpeg(current.Index)));
                return "wrote " + file;
            });

        if (mask is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Export foreground as PNG"))
                _exportStatus = PanelIo.Run(() =>
                {
                    Directory.CreateDirectory(_exportFolder);
                    string file = Path.Combine(_exportFolder, $"bg{current.Index:D4}-foreground.png");

                    var (pixels, w, h) = BackgroundCodec.JpegToRgba(session.GetBackgroundJpeg(current.Index));

                    File.WriteAllBytes(file, ImageCodec.RgbaToPng(
                        MaskRenderer.Compose(mask, pixels, w, h), mask.Width, mask.Height));

                    return "wrote " + file;
                });

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The foreground layer alone, on transparency -- the pixels that draw " +
                                 "in front of the player.");
        }

        var selectedBgs = _selection.Indices.Where(i => i >= 0 && i < list.Count).Select(i => list[i].Index).ToList();
        if (selectedBgs.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Export {selectedBgs.Count} selected"))
                _exportStatus = PanelIo.Run(() =>
                {
                    Directory.CreateDirectory(_exportFolder);
                    foreach (int id in selectedBgs)
                        File.WriteAllBytes(Path.Combine(_exportFolder, $"bg{id:D4}.png"),
                            BackgroundCodec.JpegToPng(session.GetBackgroundJpeg(id)));
                    return $"wrote {selectedBgs.Count:N0} PNGs to {_exportFolder}";
                });
        }

        ImGui.SameLine();
        if (ImGui.Button($"Export all {list.Count:N0}"))
            _exportStatus = PanelIo.Run(() =>
            {
                Directory.CreateDirectory(_exportFolder);
                foreach (var bg in list)
                    File.WriteAllBytes(Path.Combine(_exportFolder, $"bg{bg.Index:D4}.png"),
                        BackgroundCodec.JpegToPng(session.GetBackgroundJpeg(bg.Index)));
                return $"wrote {list.Count:N0} PNGs to {_exportFolder}";
            });

        AssetIoUi.DrawStatus(_exportStatus);

        ImGui.Separator();
        bool ready = AssetIoUi.DrawProjectNote();

        ImGui.SetNextItemWidth(420);
        ImGui.InputText("PNG to import", ref _importPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose..."))
        {
            string? picked = NativeDialogs.OpenFile("Choose a replacement background",
                                                    "PNG images\0*.png\0All files\0*.*\0");
            if (picked is not null) _importPath = picked;
        }

        ImGui.BeginDisabled(!ready || _importPath.Length == 0);
        if (ImGui.Button($"Import into bg{current.Index:D4}"))
            _importStatus = PanelIo.Run(() =>
                AssetIo.ImportBackground(session, ProjectPanel.Folder, current, _importPath));

        ImGui.EndDisabled();

        ImGui.TextDisabled("A replacement must match the dimensions exactly; the rebuild lays the " +
                           "asset region out again, so it may be larger than the original.");

        // The foreground gets its own path and its own button, deliberately not sharing the
        // background's.
        if (mask is not null)
        {
            ImGui.Spacing();
            ImGui.SeparatorText($"Foreground ({mask.Pieces.Count} pieces)");

            ImGui.SetNextItemWidth(420);
            ImGui.InputText("PNG whose alpha is the shape", ref _foregroundPath, 512);
            ImGui.SameLine();
            if (ImGui.Button("Choose...##fg"))
            {
                string? picked = NativeDialogs.OpenFile("Choose a foreground shape", "PNG images\0*.png\0All files\0*.*\0");
                if (picked is not null) _foregroundPath = picked;
            }

            ImGui.BeginDisabled(!ready || _foregroundPath.Length == 0);
            if (ImGui.Button($"Replace foreground of bg{current.Index:D4}"))
                _importStatus = PanelIo.Run(() =>
                    AssetIo.ImportForeground(session, ProjectPanel.Folder, current.Index, _foregroundPath));
            ImGui.EndDisabled();

            ImGui.TextDisabled("Opaque pixels draw in front of the player, transparent ones do not. " +
                               "Depth is kept from the original.");
        }
        AssetIoUi.DrawStatus(_importStatus);

        ImGui.EndChild();
    }
}

/// <summary>
/// The export/import row shared by the asset tabs: a folder to write into, buttons that act on the
/// selection, and one status line. Kept in one place so Textures and Characters behave the same.
/// </summary>
public static class AssetIoUi
{
    /// <summary>Draws the folder field and returns it.</summary>
    public static string DrawExportFolder(RomSession session, ref string folder, string defaultName)
    {
        if (folder.Length == 0)
            folder = Path.Combine(Path.GetDirectoryName(session.Path) ?? ".", defaultName);

        ImGui.SetNextItemWidth(420);
        ImGui.InputText("export folder", ref folder, 512);
        return folder;
    }

    /// <summary>Explains where an import goes, and whether it can go anywhere yet.</summary>
    public static bool DrawProjectNote()
    {
        string project = ProjectPanel.Folder;
        bool ready = AssetIo.HasProject(project);

        if (ready) ImGui.TextDisabled($"imports are written into {project}, then Build ROM on the Project tab");
        else ImGui.TextDisabled("to import, first Extract a project on the Project tab");

        return ready;
    }

    public static void DrawStatus(string status)
    {
        if (status.Length > 0) ImGui.TextWrapped(status);
    }

    /// <summary>Dimmed prose that wraps to the panel rather than running off the edge of it.</summary>
    public static void DisabledWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}

/// <summary>Grid of every decoded texture.</summary>
public static class TexturePanel
{
    private static readonly Selection _selection = new();

    /// <summary>Seeds a multi-selection for a headless capture, which cannot Shift+click.</summary>
    public static void PreselectRange(int start, int count) => _pendingRange = (start, count);

    /// <summary>Opens one texture by asset id, for anything linking here from elsewhere.</summary>
    public static void PreselectAsset(int assetId)
    {
        LabelUi.ClearFilter(AssetLabels.Texture);
        _pendingAsset = assetId;
    }

    private static (int Start, int Count)? _pendingRange;
    private static int _pendingAsset = -1;
    private static int _selected;

    /// <summary>Columns in the grid, measured inside the child last frame; see HandleArrowKeys below.</summary>
    private static int _columns = 1;

    private static string _exportFolder = "";
    private static string _importPath = "";

    // Export and import report separately, each under its own buttons.
    private static string _exportStatus = "";
    private static string _importStatus = "";

    public static unsafe void Draw(RomSession session, TextureCache cache)
    {
        var list = session.Textures;
        ImGui.Text($"{list.Count:N0} textures");
        ImGui.SameLine();
        string filter = LabelUi.DrawFilter(AssetLabels.Texture);

        _selection.Constrain(list.Count);
        if (_pendingRange is { } range)
        {
            _selection.SetRange(range.Start, range.Count, list.Count);
            _pendingRange = null;
        }

        if (_pendingAsset >= 0)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Item1.Index == _pendingAsset)
                {
                    _selection.Set(i);

                    // The grid draws only the rows on screen, so a selection it is not drawing has
                    // to ask to be scrolled to; the filter is cleared, so position and row match.
                    _selection.ScrollToRow = i;
                    break;
                }

            _pendingAsset = -1;
        }

        var visible = new List<int>(list.Count);
        for (int i = 0; i < list.Count; i++)
            if (AssetLabels.Matches(AssetLabels.Texture, list[i].Item1.Index, filter))
                visible.Add(i);

        // Up and down move by a whole row, which means knowing the column count -- and that is only
        // measurable inside the child, whose width differs from this one by the border and scrollbar.
        _selection.HandleArrowKeys(visible, _columns, horizontal: true);

        ImGui.BeginChild("tex-grid", new Vector2(0, ImGui.GetContentRegionAvail().Y - 330), ImGuiChildFlags.Border);

        float width = ImGui.GetContentRegionAvail().X;
        int columns = Math.Max(1, (int)(width / 92));
        _columns = columns;

        const float Thumbnail = 80f;
        float rowHeight = Thumbnail + ImGui.GetStyle().FramePadding.Y * 2 + ImGui.GetStyle().ItemSpacing.Y;
        int rows = (visible.Count + columns - 1) / columns;

        // Only the rows on screen are drawn.
        var gridClipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        gridClipper.Begin(rows, rowHeight);

        // A keyboard move can land on a row the clipper is not drawing, so it cannot scroll itself
        // into view; the offset is computed from the row instead.
        if (_selection.ScrollToRow >= 0)
        {
            float target = _selection.ScrollToRow / columns * rowHeight - ImGui.GetWindowHeight() / 2 + rowHeight;
            ImGui.SetScrollY(Math.Max(0, target));
            _selection.ScrollToRow = -1;
        }

        while (gridClipper.Step())
            for (int row = gridClipper.DisplayStart; row < gridClipper.DisplayEnd; row++)
                for (int column = 0; column < columns; column++)
                {
                    int position = row * columns + column;
                    if (position >= visible.Count) break;
                    if (column > 0) ImGui.SameLine();

                    int i = visible[position];
                    var (entry, texture) = list[i];
                    var gpu = cache.Get(entry.Index, () => (texture.ToRgba(), texture.Width, texture.Height));

                    if (ImGui.ImageButton($"##t{entry.Index}", (IntPtr)gpu.Handle, new Vector2(Thumbnail, Thumbnail)))
                        _selection.Click(i, visible);

                    // A grid of image buttons has no selected state of its own, so selection is
                    // shown by outlining the thumbnail.
                    if (_selection.Contains(i))
                        ImGui.GetWindowDrawList().AddRect(
                            ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                            ImGui.GetColorU32(new Vector4(1f, 0.72f, 0.20f, 1f)), 2f, ImDrawFlags.None, 3f);

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(LabelUi.Caption(AssetLabels.Texture, entry.Index,
                                                         $"#{entry.Index}  {texture}"));
                }

        gridClipper.End();
        ImGui.EndChild();
        LabelUi.DrawFilterSummary(filter, visible.Count, list.Count);

        if (_selection.Count == 0 && visible.Count > 0) _selection.Set(visible[0]);
        _selected = Math.Max(0, _selection.Primary);

        if (_selected < list.Count)
        {
            ImGui.BeginChild("tex-detail", Vector2.Zero, ImGuiChildFlags.None);
            var (entry, texture) = list[_selected];
            var gpu = cache.Get(entry.Index, () => (texture.ToRgba(), texture.Width, texture.Height));
            ImGui.Text($"asset #{entry.Index}   {texture}   {entry.StoredSize:N0} bytes stored");
            LabelUi.DrawEditor(AssetLabels.Texture, _selection.ToAssetIds(i => list[i].Item1.Index));
            ImGui.Image((IntPtr)gpu.Handle, new Vector2(texture.Width * 2, texture.Height * 2));
            LabelUi.DrawSelectionBadge(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                                       _selection.Count, $"#{entry.Index}");

            ImGui.Separator();
            var selectedIds = _selection.ToAssetIds(i => list[i].Item1.Index);
            AssetIoUi.DrawExportFolder(session, ref _exportFolder, "textures");

            if (ImGui.Button($"Export #{entry.Index} as PNG"))
                _exportStatus = PanelIo.Run(() => "wrote " + AssetIo.ExportTexturePng(session, entry.Index, _exportFolder));

            if (selectedIds.Count > 1)
            {
                ImGui.SameLine();
                if (ImGui.Button($"Export {selectedIds.Count} selected"))
                    _exportStatus = PanelIo.Run(() => AssetIo.ExportTextures(session, selectedIds, _exportFolder));
            }

            ImGui.SameLine();
            if (ImGui.Button($"Export all {list.Count:N0}"))
                _exportStatus = PanelIo.Run(() => AssetIo.ExportTextures(session, list.Select(t => t.Item1.Index), _exportFolder));

            AssetIoUi.DrawStatus(_exportStatus);

            ImGui.Separator();
            bool ready = AssetIoUi.DrawProjectNote();

            ImGui.SetNextItemWidth(420);
            ImGui.InputText("PNG to import", ref _importPath, 512);
            ImGui.SameLine();
            if (ImGui.Button("Choose..."))
            {
                string? picked = NativeDialogs.OpenFile("Choose a replacement texture",
                                                        "PNG images\0*.png\0All files\0*.*\0");
                if (picked is not null) _importPath = picked;
            }

            ImGui.BeginDisabled(!ready || _importPath.Length == 0);
            if (ImGui.Button($"Import into #{entry.Index}"))
                _importStatus = PanelIo.Run(() => AssetIo.ImportTexture(session, ProjectPanel.Folder, entry.Index, _importPath));
            ImGui.EndDisabled();

            ImGui.TextDisabled("A replacement of a different size is resized to the texture's own, " +
                               "which the game fixes; colours are re-quantised to its palette.");
            AssetIoUi.DrawStatus(_importStatus);
            ImGui.EndChild();
        }
    }
}

/// <summary>Runs one export or import, turning a failure into a line the panel can show.</summary>
internal static class PanelIo
{
    public static string Run(Func<string> action)
    {
        try { return action(); }
        catch (Exception ex) { return "failed: " + ex.Message; }
    }
}

/// <summary>Character viewer: 3D viewport, texture list and animation playback.</summary>
public sealed class ModelBrowserPanel
{
    /// <summary>Which models this browser shows.</summary>
    private readonly string _id;
    private readonly Func<RomSession, IReadOnlyList<ModelEntry>> _source;

    /// <summary>Whether rows lead with the slot number.</summary>
    private readonly bool _showSlot;

    public ModelBrowserPanel(string id, Func<RomSession, IReadOnlyList<ModelEntry>> source,
                             bool showSlot = false)
    {
        _id = id;
        _source = source;
        _showSlot = showSlot;
    }

    private readonly Selection _selection = new();

    private string _exportFolder = "";
    private string _importPath = "";

    // Reported separately so each message sits under the buttons that produced it.
    private string _exportStatus = "";
    private string _importStatus = "";

    private int _selected = -1;

    /// <summary>Preselects a character, so the headless window capture exercises the 3D view.</summary>
    public void Preselect(int index) => _pendingSelection = index;

    /// <summary>Selects by mesh asset id, which is stable where list position is not.</summary>
    public void PreselectMesh(int meshAssetId) => _pendingMesh = meshAssetId;

    /// <summary>Highlights a texture slot, for the headless capture.</summary>
    public void PreselectTexture(int slot) => _pendingHighlight = slot;

    private int _pendingHighlight = -1;

    private int _pendingMesh = -1;

    private int _pendingSelection = -1;


    /// <summary>
    /// Joints whose mesh part is a degenerate placeholder but which still drive children.
    /// </summary>
    private int CountDetachedLimbJoints(MeshFile mesh, PoseBank bank)
    {
        int count = 0;
        for (int j = 0; j < Math.Min(mesh.PartCount, bank.PartCount); j++)
        {
            if (bank.Joints[j].Children.Count == 0) continue;
            int vertices = mesh.Parts[j].SubMeshes.Sum(m => m.Vertices.Count);
            if (vertices is > 0 and <= 4) count++;
        }
        return count;
    }

    /// <summary>Pins the viewer to one clip and frame, so a headless capture is reproducible.</summary>
    public void PinFrame(int clip, int frame)
    {
        _pinnedClip = clip;
        _pinnedFrame = frame;
    }

    /// <summary>What the viewer resolved for the current frame, for headless diagnosis.</summary>
    public string LastPoseInfo { get; private set; } = "not drawn";

    /// <summary>Debug switch: ignore clips and draw the rest pose.</summary>
    public bool ForceRestPose;

    private int _pinnedClip = -1;
    private int _pinnedFrame = -1;
    /// <summary>Drops the decoded model so the next frame reloads it.</summary>
    public void Invalidate()
    {
        _mesh = null;
        _bank = null;
        _clips = null;
        if (_selected >= 0) _pendingSelection = _selected;
        _selected = -1;
    }

    /// <summary>Texture slots picked out in the viewport.</summary>
    private readonly HashSet<int> _highlightTextures = new();

    private MeshFile? _mesh;
    private PoseBank? _bank;
    private AnimationSet? _clips;
    private ModelEntry? _character;

    private int _clip;
    private float _time;
    private bool _playing = true;
    private float _fps = 15f;
    private int _frame;

    public void Draw(RomSession session, TextureCache cache, ModelViewport viewport, float delta)
    {
        var characters = _source(session);

        // Apply a preselection once the character list is available, taking the same path a click
        // would so the mesh is actually loaded rather than just highlighted.
        if (_pendingMesh >= 0)
        {
            int found = -1;
            for (int i = 0; i < characters.Count; i++)
                if (characters[i].MeshAssetId == _pendingMesh) { found = i; break; }
            _pendingMesh = -1;
            if (found >= 0) _pendingSelection = found;
        }

        if (_pendingSelection >= 0 && _pendingSelection < characters.Count)
        {
            _selected = _pendingSelection;
            Load(session, characters[_selected]);
            _pendingSelection = -1;
        }

        if (_pendingHighlight >= 0 && _mesh is not null)
        {
            _highlightTextures.Add(_pendingHighlight);
            _pendingHighlight = -1;
        }

        string filter = LabelUi.DrawFilter(AssetLabels.Model);

        _selection.Constrain(characters.Count);

        var visible = new List<int>(characters.Count);
        for (int i = 0; i < characters.Count; i++)
            if (AssetLabels.Matches(AssetLabels.Model, characters[i].MeshAssetId, filter))
                visible.Add(i);

        _selection.HandleArrowKeys(visible);

        ImGui.BeginChild($"{_id}-list", new Vector2(300, 0), ImGuiChildFlags.Border);
        foreach (int i in visible)
        {
            var c = characters[i];
            if (_selection.ScrollToRow >= 0 && i == _selection.Primary)
            {
                ImGui.SetScrollHereY(0.5f);
                _selection.ScrollToRow = -1;
            }

            // Kept short on purpose.
            string caption = LabelUi.Caption(AssetLabels.Model, c.MeshAssetId,
                                             _showSlot ? $"{c.Index}  mesh {c.MeshAssetId}"
                                                       : $"mesh {c.MeshAssetId}");
            if (ImGui.Selectable($"{caption}##{_id}{i}", _selection.Contains(i)))
            {
                _selection.Click(i, visible);
                int primary = _selection.Primary;
                if (primary >= 0 && primary != _selected)
                {
                    _selected = primary;
                    Load(session, characters[primary]);
                }
            }
        }
        ImGui.EndChild();
        LabelUi.DrawFilterSummary(filter, visible.Count, characters.Count);

        // A click that only removed entries can leave the primary somewhere new, and an external
        // preselect sets _selected directly, so reconcile once per frame rather than only on click.
        if (_selection.Count == 0 && _selected >= 0 && _selected < characters.Count)
            _selection.Set(_selected);
        else if (_selection.Primary >= 0 && _selection.Primary != _selected)
        {
            _selected = _selection.Primary;
            Load(session, characters[_selected]);
        }

        ImGui.SameLine();
        ImGui.BeginChild("char-view", Vector2.Zero, ImGuiChildFlags.Border);

        if (_mesh is null) { ImGui.Text("Select a character."); ImGui.EndChild(); return; }

        ImGui.Text($"mesh {_character!.MeshAssetId}   {_mesh.PartCount} parts   " +
                   $"{_mesh.TotalVertices:N0} verts   {_mesh.TotalTriangles:N0} tris   " +
                   (_bank is null ? "no rig" : $"{_bank.PartCount} joints, {_bank.PoseCount} poses"));

        LabelUi.DrawEditor(AssetLabels.Model, _selection.ToAssetIds(i => characters[i].MeshAssetId));

        DrawIo(session, characters);

        ImGui.Checkbox("Textured", ref viewport.ShowTextures); ImGui.SameLine();
        ImGui.Checkbox("Colour by part", ref viewport.ColourByPart); ImGui.SameLine();
        ImGui.Checkbox("Joints", ref viewport.ShowJoints);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mark every joint, drawn through the model. The root is yellow, the " +
                             "rest blue. Useful for checking where an imported skeleton actually is.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.SliderFloat("Zoom", ref viewport.Distance, 0.6f, 8f);

        // Available whether or not the model has clips: a rig with no animation still has a rest
        // pose, and it is the only thing there is to look at.
        if (_bank is not null)
        {
            if (ImGui.Checkbox("Rest pose", ref ForceRestPose) && ForceRestPose)
            {
                // Turning it on stops playback rather than leaving a clip running underneath, which
                // would start again from wherever it had got to.
                _playing = false;
                _time = 0;
                _frame = 0;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show the model with no rotation applied -- the pose the skeleton " +
                                 "is built in. The same pose the exported \"rest\" clip holds.");

            ImGui.SameLine();
        }

        if (_clips is not null && _clips.Clips.Count > 0)
        {
            // The clip controls do nothing while the rest pose is showing, so they are disabled
            // rather than left looking live.
            ImGui.BeginDisabled(ForceRestPose);

            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderInt("Clip", ref _clip, 0, _clips.Clips.Count - 1)) { _time = 0; _frame = 0; }
            ImGui.SameLine();
            ImGui.Checkbox("Play", ref _playing);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.SliderFloat("FPS", ref _fps, 1f, 60f);

            ImGui.EndDisabled();

            int detached = _bank is null ? 0 : CountDetachedLimbJoints(_mesh, _bank);
            if (detached > 0)
                ImGui.TextDisabled($"note: {detached} limb joint(s) in this model have no geometry of " +
                                   "their own, so those limbs separate during strong movement. " +
                                   "It is how the model is built, and exporting or editing it is unaffected.");

            if (_pinnedClip >= 0)
            {
                _clip = Math.Clamp(_pinnedClip, 0, _clips.Clips.Count - 1);
                _playing = false;
            }

            var clip = _clips.Clips[_clip];
            if (_pinnedFrame >= 0 && clip.FrameCount > 0)
                _frame = Math.Clamp(_pinnedFrame, 0, clip.FrameCount - 1);
            else if (_playing && !ForceRestPose && clip.FrameCount > 0)
            {
                _time += delta * _fps;
                _frame = (int)(_time % clip.FrameCount);
            }

            ImGui.SameLine();
            if (ForceRestPose) ImGui.TextDisabled("rest pose -- clip ignored");
            else ImGui.Text($"frame {_frame + 1}/{clip.FrameCount}");
        }

        // Orbit with a left-drag anywhere in the viewport image.
        var size = ImGui.GetContentRegionAvail();
        int side = (int)Math.Max(200, Math.Min(size.X, size.Y - 8));
        viewport.Resize(side, side);

        Pose? pose = null;
        if (_bank is not null)
        {
            int poseIndex = 0;
            if (_clips is not null && _clips.Clips.Count > 0)
            {
                var clip = _clips.Clips[_clip];
                if (clip.FrameCount > 0) poseIndex = clip.PoseIndices[Math.Clamp(_frame, 0, clip.FrameCount - 1)];
            }
            if (poseIndex >= 0 && poseIndex < _bank.PoseCount) pose = _bank.GetPose(poseIndex);
            if (ForceRestPose) pose = null;

            LastPoseInfo = $"clip={_clip} frame={_frame} poseIndex={poseIndex} " +
                           $"poseCount={_bank.PoseCount} " +
                           (pose is null
                               ? "pose=NULL"
                               : "angles[0..2]=" + string.Join(" ", pose.Angles.Take(3).Select(a => $"({a.X},{a.Y},{a.Z})")));
        }
        else LastPoseInfo = "bank=NULL";

        viewport.HighlightTextures.Clear();
        foreach (int slot in _highlightTextures) viewport.HighlightTextures.Add(slot);

        viewport.SetMesh(_mesh, _bank, pose);
        viewport.Render(index =>
        {
            if (_character is null || index < 0 || index >= _character.Textures.TextureIds.Count) return null;
            int id = _character.Textures.TextureIds[index];
            if (!session.TryGetAsset(id, out var data) || !TextureFile.TryParse(data, out var tex)) return null;
            return cache.Get(id, () => (tex.ToRgba(), tex.Width, tex.Height));
        });

        // glTF and OpenGL disagree on image origin, so flip V when displaying the framebuffer.
        ImGui.Image((IntPtr)viewport.ColourTexture, new Vector2(side, side), new Vector2(0, 1), new Vector2(1, 0));
        LabelUi.DrawSelectionBadge(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                                   _selection.Count, $"mesh {_character!.MeshAssetId}");

        bool orbiting = ImGui.IsItemHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Left);

        // The viewport is square, so on a wide window there is room beside it going spare.
        if (size.X - side > 140)
        {
            ImGui.SameLine();
            DrawTextureList(session, cache, side);
        }

        if (orbiting)
        {
            var drag = ImGui.GetIO().MouseDelta;
            viewport.Yaw += drag.X * 0.01f;
            viewport.Pitch = Math.Clamp(viewport.Pitch + drag.Y * 0.01f, -1.4f, 1.4f);
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Export and import for the selected character. glTF carries the mesh, its textures and -- when
    /// the character has a rig -- its skeleton and every clip, so one file round-trips in Blender.
    /// </summary>
    private void DrawIo(RomSession session, IReadOnlyList<ModelEntry> characters)
    {
        if (_character is null) return;

        if (!ImGui.CollapsingHeader("Export / import", ImGuiTreeNodeFlags.DefaultOpen)) return;

        var selected = _selection.Indices
                                 .Where(i => i >= 0 && i < characters.Count)
                                 .Select(i => characters[i])
                                 .ToList();
        if (selected.Count == 0) selected.Add(_character);

        AssetIoUi.DrawExportFolder(session, ref _exportFolder, "models");

        if (ImGui.Button($"Export mesh {_character.MeshAssetId} as GLB"))
            _exportStatus = PanelIo.Run(() => AssetIo.ExportCharacterGltf(session, _character, _exportFolder, true));

        ImGui.SameLine();
        if (ImGui.Button("Export without animation"))
            _exportStatus = PanelIo.Run(() => AssetIo.ExportCharacterGltf(session, _character, _exportFolder, false, withRig: true));

        if (selected.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Export {selected.Count} selected"))
                _exportStatus = PanelIo.Run(() => AssetIo.ExportCharacters(session, selected, _exportFolder, true));

            ImGui.SameLine();
            if (ImGui.Button($"Export {selected.Count} selected without animation"))
                _exportStatus = PanelIo.Run(() => AssetIo.ExportCharacters(session, selected, _exportFolder, false, withRig: true));
        }

        AssetIoUi.DrawStatus(_exportStatus);

        ImGui.Separator();
        bool ready = AssetIoUi.DrawProjectNote();

        ImGui.SetNextItemWidth(420);
        ImGui.InputText("glTF to import", ref _importPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose..."))
        {
            string? picked = NativeDialogs.OpenFile("Choose a model",
                                                    "glTF binary\0*.glb\0glTF\0*.gltf\0All files\0*.*\0");
            if (picked is not null) _importPath = picked;
        }

        ImGui.BeginDisabled(!ready || _importPath.Length == 0);

        if (ImGui.Button($"Import model into mesh {_character.MeshAssetId}"))
            _importStatus = PanelIo.Run(() => AssetIo.ImportMesh(session, ProjectPanel.Folder, _character, _importPath));

        ImGui.SameLine();
        if (ImGui.Button("Import animation"))
            _importStatus = PanelIo.Run(() => AssetIo.ImportAnimation(session, ProjectPanel.Folder, _character, _importPath));

        ImGui.EndDisabled();

        ImGui.TextDisabled("Model import brings the file's textures with it, matched to slots by " +
                           "their tex00, tex01 ... material names -- \nrename one and its image is " +
                           "skipped. Textures of the wrong size are resized to the slot's own, and " +
                           "re-quantised to its palette. Geometry keeps\nthe part count and " +
                           "texture slots. Moving bones in edit mode is carried over too -- the " +
                           "skeleton is rewritten and the geometry\nmeasured against its new joints. " +
                           "Animation import " +
                           "rewrites the poses only -- clip lengths and order are stored separately " +
                           "and are left alone.");
        AssetIoUi.DrawStatus(_importStatus);
        ImGui.Separator();
    }

    /// <summary>The texture slots this model actually draws from, beside the viewport.</summary>
    private void DrawTextureList(RomSession session, TextureCache cache, int height)
    {
        ImGui.BeginChild($"{_id}-texlist", new Vector2(0, height), ImGuiChildFlags.Border);

        var triangles = new Dictionary<int, int>();
        if (_mesh is not null)
            foreach (var part in _mesh.Parts)
                foreach (var sub in part.SubMeshes)
                {
                    if (sub.TextureIndex < 0) continue;
                    triangles.TryGetValue(sub.TextureIndex, out int count);
                    triangles[sub.TextureIndex] = count + sub.Triangles.Count;
                }

        ImGui.TextDisabled($"{triangles.Count} texture(s) in use");
        ImGui.Separator();

        if (triangles.Count == 0)
        {
            ImGui.TextWrapped("This model draws no textured geometry.");
            ImGui.EndChild();
            return;
        }

        foreach (int slot in triangles.Keys.OrderBy(k => k))
        {
            int assetId = _character is not null && slot < _character.Textures.TextureIds.Count
                ? _character.Textures.TextureIds[slot]
                : -1;

            string size = "";

            if (assetId >= 0 && session.TryGetAsset(assetId, out var data)
                && TextureFile.TryParse(data, out var texture))
            {
                var gpu = cache.Get(assetId, () => (texture.ToRgba(), texture.Width, texture.Height));
                ImGui.Image((IntPtr)gpu.Handle, new Vector2(28, 28));
                ImGui.SameLine();

                size = $"{texture.Width}x{texture.Height}   ";
            }

            bool on = _highlightTextures.Contains(slot);
            string caption = assetId >= 0
                ? $"tex{slot:D2}   {assetId}   {size}{triangles[slot]:N0} tris"
                : $"tex{slot:D2}   (no texture)   {triangles[slot]:N0} tris";

            if (ImGui.Selectable($"{caption}##{_id}tex{slot}", on,
                                 ImGuiSelectableFlags.AllowDoubleClick))
            {
                // Double-click opens the texture where it can be exported or replaced.
                if (assetId >= 0 && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    TexturePanel.PreselectAsset(assetId);
                    Program.ShowTab("Textures");
                    continue;
                }

                // Ctrl adds or removes one; a plain click means "just this one", and clicking the
                // only highlighted slot again clears it.
                if (Selection.CtrlHeld)
                {
                    if (!_highlightTextures.Remove(slot)) _highlightTextures.Add(slot);
                }
                else if (on && _highlightTextures.Count == 1)
                {
                    _highlightTextures.Clear();
                }
                else
                {
                    _highlightTextures.Clear();
                    _highlightTextures.Add(slot);
                }
            }
        }

        ImGui.Separator();
        AssetIoUi.DisabledWrapped("Click to highlight in the view, ctrl-click to add or remove, " +
                                  "double-click to open in Textures.");

        ImGui.EndChild();
    }

    private void Load(RomSession session, ModelEntry character)
    {
        _highlightTextures.Clear();          // a slot number means something different on a new model
        _character = character;
        _mesh = session.LoadMesh(character.MeshAssetId);
        _bank = session.LoadPoseBank(character);
        _clips = session.LoadAnimations(character);
        _clip = 0;
        _frame = 0;
        _time = 0;
    }
}

/// <summary>Table of the sample bank.</summary>
public static class SoundPanel
{
    private static readonly Selection _selection = new();
    private static int _selected = -1;
    private static short[]? _pcm;
    private static string _exportPath = "";
    private static string _status = "";
    private static bool _loop;
    private static byte[]? _wav;

    /// <summary>Drops the decoded sample so the waveform and playback re-read the bank.</summary>
    public static void Invalidate()
    {
        _pcm = null;
        _wav = null;
        AudioPlayer.Stop();
    }

    private static string _importPath = "";
    private static string _importStatus = "";

    /// <summary>Preselects a sample so a headless capture has something to draw.</summary>
    public static void Preselect(int index)
    {
        _selected = index;
        _selection.Set(index);
    }

    /// <summary>Seeds a multi-selection for a headless capture, which cannot Shift+click.</summary>
    public static void PreselectRange(int start, int count) => _pendingRange = (start, count);

    private static (int Start, int Count)? _pendingRange;

    /// <summary>
    /// Asks the panel to start playback on its next draw, so a headless run can verify that the
    /// audio device actually accepted the buffer rather than only that the button exists.
    /// </summary>
    public static void RequestPlay() => _playRequested = true;

    private static bool _playRequested;

    /// <summary>What the last play attempt did, for the headless check to report.</summary>
    public static string LastPlaybackResult { get; private set; } = "no playback attempted";

    public static unsafe void Draw(RomSession session)
    {
        var sound = session.Sounds;
        ImGui.Text($"{sound.Samples.Count:N0} samples   {sound.SampleData.Length:N0} bytes   " +
                   $"{sound.Samples.Sum(s => s.Seconds) / 60:0.0} minutes");

        // The transport is drawn above the list but steps through it, so the visible order is built
        // here rather than at the table.
        string filter = LabelUi.Filter(AssetLabels.Sound);
        var visible = new List<int>(sound.Samples.Count);
        for (int i = 0; i < sound.Samples.Count; i++)
            if (AssetLabels.Matches(AssetLabels.Sound, sound.Samples[i].Index, filter))
                visible.Add(i);

        SortVisible(sound, visible, _sortColumn, _sortAscending);

        if (_pendingStep != 0)
        {
            Step(sound, visible, _pendingStep);
            _pendingStep = 0;
        }

        // The arrows only move the selection; unlike the transport buttons they do not start
        // playback, because holding a key down through a bank of samples should not fire off a
        // hundred overlapping sounds.
        if (_selection.HandleArrowKeys(visible))
        {
            _selected = _selection.Primary;
            _scrollToRow = _selection.ScrollToRow;
            _selection.ScrollToRow = -1;
            _pcm = null;
            _wav = null;
            _status = "";
            AudioPlayer.Stop();
        }

        if (_selected >= 0 && _selected < sound.Samples.Count)
        {
            var s = sound.Samples[_selected];
            _pcm ??= sound.Decode(s);

            if (_playRequested)
            {
                _playRequested = false;
                _wav ??= sound.ToWav(s);
                bool started = AudioPlayer.Play(_wav, _selected, s.Seconds, _loop);
                LastPlaybackResult = $"sample {s.Index} ({_wav.Length:N0} byte wav): " +
                                     (started ? "accepted by the audio device" : "REFUSED by the audio device");
            }

            DrawWaveform(_pcm, s);
            LabelUi.DrawSelectionBadge(_lastWaveformMin, _lastWaveformMax, _selection.Count, $"sample {s.Index}");
            LabelUi.DrawEditor(AssetLabels.Sound, _selection.ToAssetIds(i => sound.Samples[i].Index));
            DrawTransport(sound, s);

            if (_exportPath.Length == 0)
                _exportPath = Path.Combine(Path.GetDirectoryName(session.Path) ?? ".", "sounds");

            ImGui.SetNextItemWidth(420);
            ImGui.InputText("export folder", ref _exportPath, 512);

            if (ImGui.Button($"Export sample {s.Index} as WAV"))
            {
                try
                {
                    Directory.CreateDirectory(_exportPath);
                    string file = Path.Combine(_exportPath, $"snd{s.Index:D4}_{s.SampleRate}hz.wav");
                    File.WriteAllBytes(file, sound.ToWav(s));
                    _status = "wrote " + file;
                }
                catch (Exception ex) { _status = "export failed: " + ex.Message; }
            }

            ImGui.SameLine();
            if (ImGui.Button("Export all"))
            {
                try
                {
                    Directory.CreateDirectory(_exportPath);
                    foreach (var x in sound.Samples)
                        File.WriteAllBytes(Path.Combine(_exportPath, $"snd{x.Index:D4}_{x.SampleRate}hz.wav"),
                                           sound.ToWav(x));
                    _status = $"wrote {sound.Samples.Count:N0} WAVs to {_exportPath}";
                }
                catch (Exception ex) { _status = "export failed: " + ex.Message; }
            }

            ImGui.Separator();
            bool soundReady = AssetIoUi.DrawProjectNote();

            ImGui.SetNextItemWidth(420);
            ImGui.InputText("WAV to import", ref _importPath, 512);
            ImGui.SameLine();
            if (ImGui.Button("Choose..."))
            {
                string? picked = NativeDialogs.OpenFile("Choose a replacement sample",
                                                        "WAV audio\0*.wav\0All files\0*.*\0");
                if (picked is not null) _importPath = picked;
            }

            ImGui.BeginDisabled(!soundReady || _importPath.Length == 0);
            if (ImGui.Button($"Import into sample {s.Index}"))
            {
                _importStatus = PanelIo.Run(() =>
                    AssetIo.ImportSound(session, ProjectPanel.Folder, s.Index, _importPath));

                // The bank is two assets and both just changed; re-read so the waveform above shows
                // the new audio rather than the old.
                session.Overrides.Scan(ProjectPanel.Folder, session.Rom);
                _pcm = null;
                _wav = null;
            }
            ImGui.EndDisabled();

            ImGui.TextDisabled("16-bit PCM WAV. The project's own WAVs under " +
                               "assets/sounds/samples can also be edited directly and are folded in " +
                               "by Build.");
            AssetIoUi.DrawStatus(_importStatus);
            if (_status.Length > 0) ImGui.TextUnformatted(_status);
            ImGui.Separator();
        }

        // Draws the box; the value was already read above.
        LabelUi.DrawFilter(AssetLabels.Sound);
        LabelUi.DrawFilterSummary(filter, visible.Count, sound.Samples.Count);
        _selection.Constrain(sound.Samples.Count);
        if (_selection.Count == 0 && _selected >= 0) _selection.Set(_selected);
        if (_pendingRange is { } range)
        {
            _selection.SetRange(range.Start, range.Count, sound.Samples.Count);
            _pendingRange = null;
        }

        if (!ImGui.BeginTable("sound-list", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                           ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                                           ImGuiTableFlags.Sortable)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (int column = 0; column < SortableColumns.Length; column++)
        {
            // Sorting the label column would order by the text the user typed, which is not a
            // property of the sample; the rest are.
            var flags = column == 1 ? ImGuiTableColumnFlags.NoSort : ImGuiTableColumnFlags.None;

            // Largest first is the useful default for a size: the long samples are the dialogue.
            if (column == 3) flags |= ImGuiTableColumnFlags.PreferSortDescending;

            // The id only ever needs four digits (plus room for the sort arrow), so it is fixed at
            // that and the space goes to the label, which is the one column with long text.
            if (column == 0)
                ImGui.TableSetupColumn(SortableColumns[column], flags | ImGuiTableColumnFlags.WidthFixed,
                                       ImGui.CalcTextSize("0000").X + ImGui.GetFontSize());
            else
                ImGui.TableSetupColumn(SortableColumns[column], flags | ImGuiTableColumnFlags.WidthStretch,
                                       column == 1 ? 4f : 1f);
        }
        ImGui.TableHeadersRow();

        ReadSortOrder();

        // Bring a stepped-to row into view.
        if (_scrollToRow >= 0)
        {
            float rowHeight = ImGui.GetTextLineHeightWithSpacing();
            float target = _scrollToRow * rowHeight - ImGui.GetWindowHeight() / 2 + rowHeight;
            ImGui.SetScrollY(Math.Max(0, target));
            _scrollToRow = -1;
        }

        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(visible.Count);
        while (clipper.Step())
            for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                int i = visible[row];
                var s = sound.Samples[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{s.Index}##snd{i}", _selection.Contains(i), ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selection.Click(i, visible);
                    int primary = _selection.Primary;
                    if (primary != _selected)
                    {
                        _selected = primary;
                        _pcm = null;
                        _wav = null;
                        _status = "";
                        AudioPlayer.Stop();
                    }
                }
                ImGui.TableNextColumn(); ImGui.Text(AssetLabels.Get(AssetLabels.Sound, s.Index));
                ImGui.TableNextColumn(); ImGui.Text($"0x{s.Offset:X7}");
                ImGui.TableNextColumn(); ImGui.Text($"{s.StoredSize:N0}");
                ImGui.TableNextColumn(); ImGui.Text($"{s.SampleRate} Hz");
                ImGui.TableNextColumn(); ImGui.Text($"{s.Seconds:0.00}s");
                ImGui.TableNextColumn(); ImGui.Text(s.IsLooping ? $"{s.LoopStart}..{s.LoopEnd}" : "-");
            }
        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>Play, stop and loop for the selected sample.</summary>
    private static void DrawTransport(SoundDirectory sound, SoundSample sample)
    {
        bool playingThis = AudioPlayer.IsPlaying && AudioPlayer.PlayingIndex == _selected;

        if (!AudioPlayer.Supported)
        {
            ImGui.TextDisabled(AudioPlayer.Unavailable + " Export to WAV to listen elsewhere.");
            return;
        }

        // Previous / next step through the list and play what they land on, so a bank of 1,192
        // samples can be auditioned without going back to the table between each one.
        if (ImGui.ArrowButton("##prev-sound", ImGuiDir.Left)) _pendingStep = -1;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play the previous sample");

        ImGui.SameLine();
        if (playingThis)
        {
            if (ImGui.Button("Stop", new Vector2(90, 0))) AudioPlayer.Stop();
        }
        else if (ImGui.Button("Play", new Vector2(90, 0)))
        {
            _wav ??= sound.ToWav(sample);
            if (!AudioPlayer.Play(_wav, _selected, sample.Seconds, _loop))
                _status = "Could not play this sample; the audio device refused it.";
        }

        ImGui.SameLine();
        if (ImGui.ArrowButton("##next-sound", ImGuiDir.Right)) _pendingStep = 1;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play the next sample");

        ImGui.SameLine();
        if (ImGui.Checkbox("Loop", ref _loop) && playingThis)
        {
            // Restart so the change takes effect now rather than at the next press.
            _wav ??= sound.ToWav(sample);
            AudioPlayer.Play(_wav, _selected, sample.Seconds, _loop);
        }

        ImGui.SameLine();
        if (playingThis)
            ImGui.Text($"{AudioPlayer.Position:0.00} / {sample.Seconds:0.00}s");
        else
            ImGui.TextDisabled($"{sample.Seconds:0.00}s at {sample.SampleRate} Hz");
    }

    private static readonly string[] SortableColumns =
        { "id", "label", "offset", "stored", "rate", "length", "loop" };

    /// <summary>The column the list is ordered by, and which way.</summary>
    private static int _sortColumn;
    private static bool _sortAscending = true;

    /// <summary>Takes the order from the table's headers, once ImGui reports it has changed.</summary>
    private static unsafe void ReadSortOrder()
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.NativePtr is null || !specs.SpecsDirty) return;

        if (specs.SpecsCount > 0)
        {
            var first = specs.Specs;
            _sortColumn = first.ColumnIndex;
            _sortAscending = first.SortDirection != ImGuiSortDirection.Descending;
        }
        else
        {
            _sortColumn = 0;
            _sortAscending = true;
        }

        specs.SpecsDirty = false;
    }

    /// <summary>Orders the visible rows by one of the sample's own properties.</summary>
    public static void SortVisible(SoundDirectory sound, List<int> visible, int column, bool ascending)
    {
        if (column == 0 && ascending) return;                // already in id order

        Comparison<int> compare = column switch
        {
            2 => (a, b) => sound.Samples[a].Offset.CompareTo(sound.Samples[b].Offset),
            3 => (a, b) => sound.Samples[a].StoredSize.CompareTo(sound.Samples[b].StoredSize),
            4 => (a, b) => sound.Samples[a].SampleRate.CompareTo(sound.Samples[b].SampleRate),
            5 => (a, b) => sound.Samples[a].Seconds.CompareTo(sound.Samples[b].Seconds),
            6 => (a, b) => sound.Samples[a].IsLooping.CompareTo(sound.Samples[b].IsLooping),
            _ => (a, b) => sound.Samples[a].Index.CompareTo(sound.Samples[b].Index),
        };

        // Ties broken by id, so equal sizes keep a stable, meaningful order rather than an arbitrary
        // one that shifts as the list is refiltered.
        visible.Sort((a, b) =>
        {
            int result = compare(a, b);
            if (!ascending) result = -result;
            return result != 0 ? result : sound.Samples[a].Index.CompareTo(sound.Samples[b].Index);
        });
    }

    /// <summary>
    /// Which way the arrow buttons asked to move, applied at the top of the next Draw.
    /// </summary>
    private static int _pendingStep;

    /// <summary>Row in the visible order to scroll to, or -1. Set by a step, consumed by the table.</summary>
    private static int _scrollToRow = -1;

    /// <summary>Moves the selection one place through the list and plays what it lands on.</summary>
    private static void Step(SoundDirectory sound, List<int> visible, int direction)
    {
        if (visible.Count == 0) return;

        int at = visible.IndexOf(_selected);

        // Nothing selected yet, or the selection is hidden by the filter: start at whichever end the
        // arrow points away from.
        int next = at < 0
            ? (direction > 0 ? 0 : visible.Count - 1)
            : at + direction;

        if (next < 0 || next >= visible.Count) return;

        _selected = visible[next];
        _selection.Set(_selected);
        _scrollToRow = next;
        _pcm = null;
        _wav = null;
        _status = "";
        AudioPlayer.Stop();

        var sample = sound.Samples[_selected];
        _wav = sound.ToWav(sample);
        if (!AudioPlayer.Play(_wav, _selected, sample.Seconds, _loop))
            _status = "Could not play this sample; the audio device refused it.";
    }

    /// <summary>Min/max envelope per pixel column.</summary>
    private static Vector2 _lastWaveformMin, _lastWaveformMax;

    private static void DrawWaveform(short[] pcm, SoundSample sample)
    {
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 120);
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        // Recorded for the multi-selection badge, which is drawn by the caller after the waveform so
        // it lands on top of it.
        _lastWaveformMin = origin;
        _lastWaveformMax = origin + size;

        uint background = ImGui.GetColorU32(new Vector4(0.09f, 0.09f, 0.11f, 1f));
        uint axis = ImGui.GetColorU32(new Vector4(0.35f, 0.35f, 0.40f, 1f));
        uint wave = ImGui.GetColorU32(new Vector4(0.40f, 0.80f, 0.45f, 1f));
        uint loop = ImGui.GetColorU32(new Vector4(0.90f, 0.65f, 0.20f, 1f));

        draw.AddRectFilled(origin, origin + size, background);
        float mid = origin.Y + size.Y / 2;
        draw.AddLine(new Vector2(origin.X, mid), new Vector2(origin.X + size.X, mid), axis);

        int columns = Math.Max(1, (int)size.X);
        for (int x = 0; x < columns && pcm.Length > 0; x++)
        {
            int from = (int)((long)x * pcm.Length / columns);
            int to = Math.Max(from + 1, (int)((long)(x + 1) * pcm.Length / columns));
            short lo = short.MaxValue, hi = short.MinValue;
            for (int i = from; i < to && i < pcm.Length; i++)
            {
                if (pcm[i] < lo) lo = pcm[i];
                if (pcm[i] > hi) hi = pcm[i];
            }
            float y0 = mid - hi / 32768f * (size.Y / 2);
            float y1 = mid - lo / 32768f * (size.Y / 2);
            draw.AddLine(new Vector2(origin.X + x, y0), new Vector2(origin.X + x, y1), wave);
        }

        if (sample.IsLooping && sample.DeclaredLength > 0)
            foreach (uint point in new[] { sample.LoopStart, sample.LoopEnd })
            {
                float x = origin.X + size.X * point / sample.DeclaredLength;
                draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), loop);
            }

        // Playhead.
        if (AudioPlayer.IsPlaying && AudioPlayer.PlayingIndex == _selected && sample.Seconds > 0)
        {
            uint head = ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.98f, 0.9f));
            float t = (float)Math.Clamp(AudioPlayer.Position / sample.Seconds, 0, 1);
            float x = origin.X + size.X * t;
            draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), head, 2f);
        }

        ImGui.Dummy(size);
        ImGui.Text($"sample {sample.Index}   {sample.SampleRate} Hz   {pcm.Length:N0} samples   " +
                   $"{sample.Seconds:0.00}s" + (sample.IsLooping ? "   loop marked in orange" : ""));
    }
}

/// <summary>Assets edited in the project folder but not yet built into a ROM.</summary>
public static class OverridePanel
{
    /// <summary>The asset whose Revert button is waiting for a second click, or -1.</summary>
    private static int _confirming = -1;

    private static string _status = "";

    private static readonly string[] Columns = { "use edit", "asset", "what", "size", "revert", "file" };

    private const int UseColumn = 0;
    private const int AssetColumn = 1;
    private const int WhatColumn = 2;
    private const int SizeColumn = 3;
    private const int RevertColumn = 4;
    private const int FileColumn = 5;

    private static int _sortColumn = AssetColumn;
    private static bool _sortAscending = true;

    /// <summary>Takes the order from the table's headers, once ImGui reports it has changed.</summary>
    private static unsafe void ReadSortOrder()
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.NativePtr is null || !specs.SpecsDirty) return;

        if (specs.SpecsCount > 0)
        {
            var first = specs.Specs;
            _sortColumn = first.ColumnIndex;
            _sortAscending = first.SortDirection != ImGuiSortDirection.Descending;
        }
        else
        {
            _sortColumn = AssetColumn;
            _sortAscending = true;
        }

        specs.SpecsDirty = false;
    }

    /// <summary>Orders the rows by one of the edit's own properties.</summary>
    public static void SortItems(List<ProjectOverrides.Item> rows, int column, bool ascending)
    {
        Comparison<ProjectOverrides.Item> compare = column switch
        {
            WhatColumn => (a, b) => string.CompareOrdinal(a.Category, b.Category),
            SizeColumn => (a, b) => a.ProjectSize.CompareTo(b.ProjectSize),
            FileColumn => (a, b) => string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => a.AssetId.CompareTo(b.AssetId),
        };

        // Ties broken by asset id, so equal categories or sizes keep a stable order rather than one
        // that shifts every time the project is rescanned.
        rows.Sort((a, b) =>
        {
            int result = compare(a, b);
            if (!ascending) result = -result;
            return result != 0 ? result : a.AssetId.CompareTo(b.AssetId);
        });
    }

    public static void Draw(RomSession session)
    {
        var overrides = session.Overrides;

        if (ImGui.Button("Rescan")) overrides.Scan(ProjectPanel.Folder, session.Rom);
        ImGui.SameLine();

        bool enabled = overrides.Enabled;
        if (ImGui.Checkbox("Show project edits", ref enabled)) overrides.Enabled = enabled;

        ImGui.SameLine();
        ImGui.TextDisabled(overrides.Folder.Length > 0 ? overrides.Folder : "(no project folder)");

        if (overrides.Count == 0)
        {
            ImGui.Separator();
            ImGui.TextWrapped(overrides.Message);
            ImGui.TextDisabled(
                "Import a background, texture or model on its own tab, or edit a file in the project " +
                "folder directly, and it will be listed here.");
            return;
        }

        ImGui.Separator();
        AssetIoUi.DrawStatus(_status);
        ImGui.TextWrapped(overrides.Enabled
            ? $"{overrides.Count:N0} asset(s) differ from the ROM. The editor is showing the edited " +
              "version of everything ticked below."
            : $"{overrides.Count:N0} asset(s) differ from the ROM, but the editor is showing the ROM. " +
              "Tick \"Show project edits\" to see them.");

        if (ImGui.Button("Use all edits")) overrides.SetAllEnabled(true);
        ImGui.SameLine();
        if (ImGui.Button("Use all ROM originals")) overrides.SetAllEnabled(false);

        ImGui.TextDisabled("Those two only change what is shown. Revert, in the table, rewrites the " +
                           "file in the project folder from the ROM and cannot be undone.");

        ImGui.Separator();

        if (!ImGui.BeginTable("overrides", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                              ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                                              ImGuiTableFlags.Sortable)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (int column = 0; column < Columns.Length; column++)
        {
            var flags = ImGuiTableColumnFlags.None;

            // The tick box and the Revert button are controls, not properties.
            if (column is UseColumn or RevertColumn) flags |= ImGuiTableColumnFlags.NoSort;

            // Biggest first is the useful default for a size -- the large edits are the ones that
            // decide whether a build still fits.
            if (column == SizeColumn) flags |= ImGuiTableColumnFlags.PreferSortDescending;

            ImGui.TableSetupColumn(Columns[column], flags);
        }

        ImGui.TableHeadersRow();
        ReadSortOrder();

        // A copy, because Items is a scan's immutable snapshot and the scan runs on another thread.
        var rows = overrides.Items.ToList();
        SortItems(rows, _sortColumn, _sortAscending);

        foreach (var item in rows)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            bool use = overrides.IsEnabled(item.AssetId);
            if (ImGui.Checkbox($"##use{item.AssetId}", ref use)) overrides.SetEnabled(item.AssetId, use);

            ImGui.TableNextColumn();
            ImGui.Text(item.Ids.Count > 1 ? $"{item.AssetId} (+{item.Ids.Count - 1})" : item.AssetId.ToString());

            ImGui.TableNextColumn(); ImGui.Text(item.Category);
            ImGui.TableNextColumn(); ImGui.Text(item.SizeChange);

            ImGui.TableNextColumn();
            if (_confirming == item.AssetId)
            {
                if (ImGui.SmallButton($"discard?##revert{item.AssetId}"))
                {
                    _status = PanelIo.Run(() =>
                        AssetIo.RevertOverride(session, ProjectPanel.Folder, item.AssetId));

                    _confirming = -1;
                    overrides.Scan(ProjectPanel.Folder, session.Rom);
                }

                ImGui.SameLine();
                if (ImGui.SmallButton($"no##cancel{item.AssetId}")) _confirming = -1;
            }
            else if (ImGui.SmallButton($"revert##revert{item.AssetId}"))
            {
                _confirming = item.AssetId;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Overwrite this file in the project with the ROM's own copy. " +
                                 "The edit is discarded and cannot be recovered.");

            ImGui.TableNextColumn(); ImGui.TextDisabled(item.File);
        }

        ImGui.EndTable();
    }
}

/// <summary>Raw view of the asset directory.</summary>
public static class AssetPanel
{
    private static readonly Selection _selection = new();
    private static int _selectedAsset = -1;

    /// <summary>Row in the visible order to scroll to after a keyboard move, or -1.</summary>
    private static int _assetScrollToRow = -1;

    public static unsafe void Draw(RomSession session)
    {
        var entries = session.Assets.Entries;
        ImGui.Text($"{entries.Count:N0} assets   base ROM 0x{session.Assets.AssetBaseRomOffset:X7}   " +
                   $"build {session.Assets.BuildStamp}");

        string filter = LabelUi.DrawFilter(AssetLabels.Asset);

        var visible = new List<int>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
            if (AssetLabels.Matches(AssetLabels.Asset, entries[i].Index, filter, entries[i].Kind.ToString()))
                visible.Add(i);

        LabelUi.DrawFilterSummary(filter, visible.Count, entries.Count);

        _selection.Constrain(entries.Count);
        if (_selection.HandleArrowKeys(visible))
        {
            _assetScrollToRow = _selection.ScrollToRow;
            _selection.ScrollToRow = -1;
        }
        _selectedAsset = _selection.Primary;
        if (_selectedAsset >= 0 && _selectedAsset < entries.Count)
        {
            LabelUi.DrawEditor(AssetLabels.Asset, _selection.ToAssetIds(i => entries[i].Index));
            if (_selection.IsMultiple)
                ImGui.TextDisabled($"{_selection.Count} assets selected");
        }

        if (!ImGui.BeginTable("assets", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                           ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var column in new[] { "id", "label", "rom offset", "stored", "decoded", "codec" })
            ImGui.TableSetupColumn(column);
        ImGui.TableHeadersRow();

        // Must come after the header row: before it the table's scrolling child is not the current
        // window and SetScrollY is silently dropped.
        if (_assetScrollToRow >= 0)
        {
            float rowHeight = ImGui.GetTextLineHeightWithSpacing();
            float target = _assetScrollToRow * rowHeight - ImGui.GetWindowHeight() / 2 + rowHeight;
            ImGui.SetScrollY(Math.Max(0, target));
            _assetScrollToRow = -1;
        }

        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(visible.Count);
        while (clipper.Step())
            for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                int i = visible[row];
                var e = entries[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{e.Index}##asset{i}", _selection.Contains(i), ImGuiSelectableFlags.SpanAllColumns))
                    _selection.Click(i, visible);
                ImGui.TableNextColumn(); ImGui.Text(AssetLabels.Get(AssetLabels.Asset, e.Index));
                ImGui.TableNextColumn(); ImGui.Text($"0x{e.RomOffset:X7}");
                ImGui.TableNextColumn(); ImGui.Text($"{e.StoredSize:N0}");
                ImGui.TableNextColumn(); ImGui.Text($"{e.DecompressedSize:N0}");
                ImGui.TableNextColumn(); ImGui.Text(e.Kind.ToString());
            }
        clipper.End();
        ImGui.EndTable();
    }
}

/// <summary>
/// Extract the whole ROM to a folder of loose files, edit them with anything, and build a new ROM.
/// </summary>
public static class ProjectPanel
{
    private static string _folder = "";
    private static string _outputRom = "";

    /// <summary>The ROM a build in this session actually wrote, or empty if none has.</summary>
    private static string _builtRom = "";
    private static string _log = "";
    private static Task? _work;
    private static int _done, _total;

    private static bool Busy => _work is { IsCompleted: false };

    /// <summary>The project folder the other tabs import into.</summary>
    public static string Folder => _folder;

    /// <summary>
    /// Picks the project folder when a ROM is opened, rather than waiting for this tab to be drawn.
    /// </summary>
    public static void Initialise(string romPath, Settings settings, RomFile? rom = null)
    {
        // Europe and Japan get folders of their own: their asset numbering differs from the USA builds'.
        var release = rom is null ? Re2Release.UsaRev1 : Re2Version.Detect(rom).Release;
        string name = release switch
        {
            Re2Release.Europe => "re2-project-eu",
            Re2Release.Japan => "re2-project-jp",
            _ => "re2-project"
        };
        string beside = Path.Combine(Path.GetDirectoryName(romPath) ?? ".", name);

        bool Usable(string folder)
            => AssetIo.DescribeProject(folder).Ready && (rom is null || ProjectFolder.FitsRom(folder, rom));

        // A remembered folder that still holds a usable extract wins; otherwise fall back to the
        // default location, which may itself already hold one from an earlier run.
        string? remembered = settings.LastProject;
        _folder = remembered is { Length: > 0 } && Usable(remembered)
            ? remembered
            : beside;

        // Absolute and tidied: this string is shown in the UI and written to settings, and a path
        // carrying ".." segments is both ugly and awkward to compare against later.
        try { _folder = Path.GetFullPath(_folder); } catch (ArgumentException) { }

        // Having found one, write it down.
        if (Usable(_folder)) settings.RememberProject(_folder);

        _outputRom = Path.Combine(Path.GetDirectoryName(romPath) ?? ".", "re2-modified.z64");
    }

    /// <summary>The project's own release against the open ROM's, or null when they agree.</summary>
    private static string? ProjectMismatch(string folder, RomFile rom)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(
                SharedFile.ReadAllText(Path.Combine(folder, ProjectFolder.ManifestName)));

            return manifest is null ? null : ProjectFolder.ReleaseMismatch(manifest, rom);
        }
        catch (Exception) { return null; }     // an unreadable manifest is the build's problem to report
    }

    private static bool _confirmExtract;

    /// <summary>Opens the overwrite question on the next draw, for a headless capture of it.</summary>
    public static void AskBeforeExtract() => _confirmExtract = true;
    private const string ConfirmExtractPopup = "Overwrite the project?";

    /// <summary>The OK / Cancel question asked before extracting over an existing project.</summary>
    private static void DrawExtractConfirmation(RomSession session)
    {
        if (_confirmExtract)
        {
            ImGui.OpenPopup(ConfirmExtractPopup);
            _confirmExtract = false;
        }

        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(ConfirmExtractPopup, ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.Text("This folder already holds an extracted project:");
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), _folder);
        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 36);
        ImGui.TextWrapped("Extracting again rewrites every asset file in it with the ROM's original, so " +
                          "ALL of your existing edits in this project folder (imported sounds, textures, " +
                          "models, text and anything edited by hand) will be completely overwritten. " +
                          "This cannot be undone.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Text("Are you sure you want to proceed?");
        ImGui.Spacing();

        if (ImGui.Button("OK", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
            StartExtract(session);
        }
        ImGui.SameLine();
        bool cancel = ImGui.Button("Cancel", new Vector2(120, 0));

        // Cancel is the safe default: keyboard focus starts on it, and Escape takes it too.
        ImGui.SetItemDefaultFocus();
        if (cancel || ImGui.IsKeyPressed(ImGuiKey.Escape))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private static void StartExtract(RomSession session)
    {
        _log = "";
        _done = 0; _total = 1;
        string folder = _folder;
        _work = Task.Run(() =>
        {
            try
            {
                // The editor scans this folder in the background to find edits, and an extract
                // rewrites every file in it.
                using var paused = session.Overrides.Pause();

                var manifest = ProjectFolder.Extract(session.Rom, folder,
                    (done, total) => { _done = done; _total = total; });

                var kinds = string.Join("   ", manifest.Blobs
                    .GroupBy(b => b.Kind)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} {g.Count():N0}"));

                Settings.Load().RememberProject(folder);

                _log = $"extracted {manifest.Blobs.Count:N0} blobs covering " +
                       $"{manifest.Blobs.Sum(b => b.Ids.Count):N0} asset ids\n{kinds}\n" +
                       $"{manifest.Blobs.Count(b => b.Ids.Count > 1):N0} blobs are shared by several ids\n" +
                       $"-> {Path.GetFullPath(folder)}";
            }
            catch (Exception ex) { _log = "extract failed: " + ex.Message; }
        });
    }

    public static void Draw(RomSession session)
    {
        if (_folder.Length == 0) Initialise(session.Path, new Settings(), session.Rom);

        var (ready, projectMessage) = AssetIo.DescribeProject(_folder);

        ImGui.TextWrapped(
            "Extract writes every asset as a loose file (compressed assets are decoded first). " +
            "Edit whatever you like, then Build to produce a new ROM. Untouched assets are copied " +
            "byte for byte, so building without edits reproduces the original cart exactly.");

        // Which cart everything here is based on.
        var version = Re2Version.Detect(session.Rom);
        ImGui.Text($"source ROM   {Path.GetFileName(session.Path)}");
        ImGui.SameLine();
        if (version.Recognised) ImGui.TextDisabled($"-- Resident Evil 2 {version}");
        else ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), $"-- {version}, treated as USA (Rev 1)");

        ImGui.Separator();

        ImGui.SetNextItemWidth(560);
        ImGui.InputText("project folder", ref _folder, 512);

        if (ready) ImGui.TextDisabled($"already extracted here -- {projectMessage}");
        else ImGui.TextDisabled(projectMessage);

        ImGui.SetNextItemWidth(560);
        ImGui.InputText("output ROM", ref _outputRom, 512);

        ImGui.BeginDisabled(Busy);

        if (ImGui.Button("Extract"))
        {
            // Extracting over an earlier extract puts every file back as the cart has it, so ask
            // first rather than silently throwing away the user's edits.
            if (File.Exists(Path.Combine(_folder, ProjectFolder.ManifestName)))
                _confirmExtract = true;
            else
                StartExtract(session);
        }

        ImGui.SameLine();

        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("Build ROM"))
        {
            _log = "";
            _done = 0; _total = 0;
            string folder = _folder, output = _outputRom;
            _work = Task.Run(() =>
            {
                try
                {
                    var rom = RomFile.Load(session.Path);
                    string? mismatch = ProjectMismatch(folder, session.Rom);
                    var result = ProjectFolder.Build(session.Rom, folder, rom);
                    rom.Save(output);

                    _log = (mismatch is null ? "" : mismatch + "\n") +
                           $"built from {Path.GetFileName(session.Path)} -- " +
                           $"Resident Evil 2 {Re2Version.Detect(session.Rom)}\n" +
                           $"rebuilt {result.BlobsRebuilt:N0} edited blobs\n" +
                           $"{result.AssetsMoved:N0} assets had to move\n" +
                           (result.AssetsMoved > 50
                               ? "\nNOTE: the region was compacted, so most assets are at new addresses.\n\n"
                               : "") +
                           $"asset region ends at 0x{result.BytesUsed:X7}, {result.BytesFree:N0} bytes free\n" +
                           (result.ByteIdentical
                               ? "\nasset region is byte-identical to the source ROM\n"
                               : "asset region differs from the source ROM (expected after an edit)\n") +
                           $"wrote {output} (checksum fixed)";

                    _builtRom = output;
                }
                catch (Exception ex) { _log = "build failed: " + ex.Message; }
            });
        }

        ImGui.SameLine();

        // Nothing to diff against until a build has produced a ROM in this session.
        bool built = _builtRom.Length > 0 && File.Exists(_builtRom);

        ImGui.SameLine();

        // Space is the one wall a project actually hits, and unused textures are where it usually
        // hides: replacing a model with one that draws fewer textures leaves the rest in the ROM,
        // loaded and paid for, with nothing recording that they stopped mattering.
        if (ImGui.Button("Find unused textures"))
            _log = PanelIo.Run(() => AssetIo.ReclaimUnusedTextures(session, ProjectPanel.Folder, apply: false));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reports textures no model draws any more, and what blanking them would save. " +
                             "Changes nothing.");

        ImGui.SameLine();
        if (ImGui.Button("Blank unused textures"))
            _log = PanelIo.Run(() => AssetIo.ReclaimUnusedTextures(session, ProjectPanel.Folder, apply: true));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rewrites each as a uniform transparent image of the same size, which costs " +
                             "almost nothing once compressed." + Environment.NewLine +
                             "Re-extracting the project puts the originals back.");

        ImGui.BeginDisabled(!built);

        if (ImGui.Button("Create Patch"))
        {
            _log = "";
            _done = 0; _total = 0;
            string original = session.Path, modified = _builtRom;
            string builtFrom = $"Resident Evil 2 {Re2Version.Detect(session.Rom)}";

            _work = Task.Run(() =>
            {
                try
                {
                    var source = SharedFile.ReadAllBytes(original);
                    var target = SharedFile.ReadAllBytes(modified);

                    var patch = BpsPatch.Create(source, target, "Created by RE2 Studio");
                    string path = Path.ChangeExtension(modified, ".bps");
                    File.WriteAllBytes(path, patch);

                    int changed = 0;
                    for (int i = 0; i < Math.Min(source.Length, target.Length); i++)
                        if (source[i] != target[i]) changed++;

                    _log = $"based on {Path.GetFileName(original)} -- {builtFrom}" + Environment.NewLine +
                           $"wrote {path}" + Environment.NewLine +
                           $"{patch.Length:N0} bytes, describing {changed:N0} changed bytes" + Environment.NewLine +
                           "\nThe patch holds only your changes, never the game's own data, so it can be " +
                           "shared.\nApplying it needs the same original ROM -- this one, the " +
                           $"{builtFrom} build:\nthe patch carries a checksum of the cart and refuses any " +
                           "other copy.";
                }
                catch (Exception ex) { _log = "patch failed: " + ex.Message; }
            });
        }

        ImGui.EndDisabled();

        if (!built)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(build a ROM first)");
        }
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        DrawExtractConfirmation(session);

        if (!ready && !Busy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(extract first)");
        }

        if (Busy)
        {
            float fraction = _total > 0 ? (float)_done / _total : 0f;
            ImGui.ProgressBar(fraction, new Vector2(560, 0), _total > 0 ? $"{_done:N0} / {_total:N0}" : "working");
        }

        if (_log.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(_log);
        }

        DrawPatcher();
    }

    private static string _patchFile = "";
    private static string _patchTargetRom = "";
    private static string _patchOutput = "";
    private static string _patchLog = "";

    /// <summary>A plain BPS patcher, for applying a downloaded romhack.</summary>
    private static void DrawPatcher()
    {
        ImGui.Separator();
        // Collapsed on every launch: applying someone else's patch is an occasional errand, not part of
        // editing.
        if (!ImGui.CollapsingHeader("BPS Patching")) return;

        ImGui.TextWrapped(
            "Applies a BPS patch to a ROM. Works with any game, not just RE2 -- a patch holds " +
            "only the hack's changes, so you supply the original ROM it was made from.");

        ImGui.SetNextItemWidth(560);
        ImGui.InputText("patch file", ref _patchFile, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose...##patch"))
        {
            string? picked = NativeDialogs.OpenFile("Choose a BPS patch", "BPS patch\0*.bps\0All files\0*.*\0");
            if (picked is not null)
            {
                _patchFile = picked;
                if (_patchOutput.Length == 0) _patchOutput = Path.ChangeExtension(picked, ".patched.z64");
            }
        }

        ImGui.SetNextItemWidth(560);
        ImGui.InputText("original ROM", ref _patchTargetRom, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose...##patchrom"))
        {
            string? picked = NativeDialogs.OpenFile("Choose the original ROM",
                                                    "ROM files\0*.z64;*.n64;*.v64;*.sfc;*.smc;*.nes;*.gba\0All files\0*.*\0");
            if (picked is not null) _patchTargetRom = picked;
        }

        ImGui.SetNextItemWidth(560);
        ImGui.InputText("save patched ROM as", ref _patchOutput, 512);

        bool ready = _patchFile.Length > 0 && _patchTargetRom.Length > 0 && _patchOutput.Length > 0;

        ImGui.BeginDisabled(!ready || Busy);

        if (ImGui.Button("Apply Patch"))
        {
            _patchLog = "";
            string patchFile = _patchFile, romFile = _patchTargetRom, outFile = _patchOutput;

            _work = Task.Run(() =>
            {
                try
                {
                    var patch = SharedFile.ReadAllBytes(patchFile);
                    var rom = SharedFile.ReadAllBytes(romFile);

                    var applied = BpsPatch.Apply(patch, rom);
                    File.WriteAllBytes(outFile, applied.Target);

                    _patchLog = $"wrote {outFile} ({applied.Target.Length:N0} bytes)" +
                                (applied.Metadata.Length > 0
                                    ? Environment.NewLine + "the patch says: " + applied.Metadata
                                    : "");
                }
                catch (Exception ex) { _patchLog = ex.Message; }
            });
        }

        ImGui.EndDisabled();

        if (!ready)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(choose a patch, a ROM and where to save)");
        }

        if (_patchLog.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextWrapped(_patchLog);
        }
    }
}

/// <summary>The readable in-game text: files, memos and diaries.</summary>
public static class TextPanel
{
    /// <summary>Drops the parsed strings so they are read again.</summary>
    public static void Invalidate() => _entries = null;

    /// <summary>Opens one string by asset id, for captures and for anything linking here.</summary>
    public static void Preselect(int assetId) => _pendingSelect = assetId;

    private static int _pendingSelect = -1;

    private static List<TextEntry>? _entries;
    private static readonly Dictionary<int, string> Edits = new();
    private static readonly Selection _selection = new();
    private static int _selected;
    private static string _buffer = "";
    private static string _jsonPath = "";
    private static string _status = "";
    private static string _saveStatus = "";
    private static string _filter = "";

    public static void Draw(RomSession session)
    {
        bool reloaded = _entries is null;
        _entries ??= TextTable.Read(session.Rom, session.Assets);

        // The edit box holds a copy of the text, not a view of it, so a reload has to refresh it --
        // otherwise, once the project scan lands, the list shows the project's version while the box
        // still shows the ROM's, and saving from that box would write the ROM's text over the
        // project's edit. An unsaved edit is left alone: that is the user's work, not a stale view.
        if (reloaded && _selected >= 0 && !Edits.ContainsKey(_selected))
        {
            var fresh = _entries.FirstOrDefault(e => e.AssetId == _selected);
            if (fresh is not null) _buffer = fresh.Text.Replace("\r\n", "\n");
        }

        if (_jsonPath.Length == 0)
            _jsonPath = Path.Combine(Path.GetDirectoryName(session.Path) ?? ".", "re2-text.json");

        ImGui.Text($"{_entries.Count:N0} text assets   " +
                   $"{_entries.Sum(e => (long)e.Text.Length):N0} characters   " +
                   $"{Edits.Count:N0} edited");
        ImGui.TextDisabled("Inventory item names are not here -- they are in overlay 1; see the Item Names tab.");

        ImGui.SetNextItemWidth(300);
        ImGui.InputText("filter", ref _filter, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(360);
        ImGui.InputText("json", ref _jsonPath, 512);

        // "Export", not "Save": this writes a side file for the command line and touches nothing in
        // the project. Two buttons both called Save that did different things would be a trap.
        ImGui.SameLine();
        if (ImGui.Button("Export JSON"))
        {
            try
            {
                var payload = _entries
                    .Select(e => new Row(e.AssetId, e.Kind.ToString(), Edits.TryGetValue(e.AssetId, out var t) ? t : e.Text))
                    .ToList();
                File.WriteAllText(_jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                _status = $"wrote {payload.Count:N0} strings to {_jsonPath}. " +
                          "Apply with: re2 text-import <rom> --project <dir> --json <file>";
            }
            catch (Exception ex) { _status = "save failed: " + ex.Message; }
        }

        if (_status.Length > 0) ImGui.TextWrapped(_status);
        ImGui.Separator();

        // Positions in _entries rather than in the filtered list, because that is what the shared
        // Selection indexes by and what a Shift range has to be measured over.
        var visible = new List<int>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            bool match = _filter.Length == 0 ||
                         e.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                         e.AssetId.ToString().Contains(_filter) ||
                         AssetLabels.Get(AssetLabels.Text, e.AssetId)
                                    .Contains(_filter, StringComparison.OrdinalIgnoreCase);
            if (match) visible.Add(i);
        }

        _selection.Constrain(_entries.Count);

        if (_pendingSelect >= 0)
        {
            int at = _entries.FindIndex(e => e.AssetId == _pendingSelect);
            if (at >= 0)
            {
                _selection.Set(at);
                _selection.ScrollToRow = at;
                _selected = _entries[at].AssetId;
                _buffer = (Edits.TryGetValue(_selected, out var pending) ? pending : _entries[at].Text)
                          .Replace("\r\n", "\n");
            }

            _pendingSelect = -1;
        }

        if (_selection.HandleArrowKeys(visible))
        {
            var moved = _entries[_selection.Primary];
            _selected = moved.AssetId;
            _buffer = (Edits.TryGetValue(moved.AssetId, out var moved_text) ? moved_text : moved.Text)
                      .Replace("\r\n", "\n");
        }

        ImGui.BeginChild("textlist", new Vector2(260, 0), ImGuiChildFlags.Border);
        foreach (int i in visible)
        {
            var e = _entries[i];
            if (_selection.ScrollToRow >= 0 && i == _selection.Primary)
            {
                ImGui.SetScrollHereY(0.5f);
                _selection.ScrollToRow = -1;
            }

            string preview = e.Text.Replace("\r\n", " ").Trim();
            if (preview.Length > 26) preview = preview[..26];
            bool edited = Edits.ContainsKey(e.AssetId);

            if (ImGui.Selectable($"{(edited ? "*" : " ")}{e.AssetId}  {preview}##t{e.AssetId}",
                                 _selection.Contains(i)))
            {
                _selection.Click(i, visible);
                int primary = _selection.Primary;
                if (primary >= 0)
                {
                    var chosen = _entries[primary];
                    _selected = chosen.AssetId;
                    _buffer = (Edits.TryGetValue(chosen.AssetId, out var t) ? t : chosen.Text)
                              .Replace("\r\n", "\n");
                }
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("textedit", new Vector2(0, 0), ImGuiChildFlags.None);

        var current = _entries.FirstOrDefault(e => e.AssetId == _selected);
        if (current is null)
        {
            ImGui.TextDisabled("Select a string on the left.");
        }
        else
        {
            ImGui.Text($"asset {current.AssetId}   {current.Kind}   original {current.Text.Length} chars");
            LabelUi.DrawEditor(AssetLabels.Text, _selection.ToAssetIds(i => _entries[i].AssetId));
            if (_selection.IsMultiple)
                ImGui.TextDisabled($"{_selection.Count} strings selected -- editing the text of {current.AssetId}");

            if (ImGui.InputTextMultiline("##body", ref _buffer, 4096,
                                         new Vector2(-1, ImGui.GetContentRegionAvail().Y - 60)))
            {
                string stored = _buffer.Replace("\n", "\r\n");
                if (stored == current.Text) Edits.Remove(current.AssetId);
                else Edits[current.AssetId] = stored;
            }

            bool ready = AssetIo.HasProject(ProjectPanel.Folder);
            bool edited = Edits.ContainsKey(current.AssetId);

            ImGui.BeginDisabled(!ready || !edited);
            if (ImGui.Button("Save"))
                _saveStatus = Save(session, current.AssetId);
            ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!ready ? "Extract a project on the Project tab first -- that is where it is saved."
                                : !edited ? "Nothing to save: this string matches the project."
                                : "Writes this string into its .txt in the project folder.");

            // The others matter too: with only a per-string Save, edits to strings not on screen
            // would sit in memory and be lost on exit without a word.
            int others = Edits.Keys.Count(id => id != current.AssetId);
            if (others > 0)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(!ready);
                if (ImGui.Button($"Save all {Edits.Count}"))
                    _saveStatus = SaveAll(session);
                ImGui.EndDisabled();
            }

            ImGui.SameLine();
            if (ImGui.Button("Revert this string"))
            {
                Edits.Remove(current.AssetId);
                _buffer = current.Text.Replace("\r\n", "\n");
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"{_buffer.Length} chars, {_buffer.Count(c => c == '\n') + 1} lines" +
                               (edited ? "   (edited, not saved)" : ""));

            if (!ready)
                ImGui.TextDisabled("To save, first Extract a project on the Project tab.");

            AssetIoUi.DrawStatus(_saveStatus);
        }

        ImGui.EndChild();
    }

    private sealed record Row(int id, string kind, string text);

    /// <summary>Saves one string and makes the panel agree with what is now on disk.</summary>
    private static string Save(RomSession session, int assetId)
    {
        if (_entries is null || !Edits.TryGetValue(assetId, out var text)) return "";

        int at = _entries.FindIndex(e => e.AssetId == assetId);
        if (at < 0) return "";

        try
        {
            string report = AssetIo.SaveText(ProjectPanel.Folder, assetId, text, _entries[at].Text.Length,
                                           session.Rom.Layout.Latin1Documents);

            // The saved text is now the baseline.
            _entries[at] = _entries[at] with { Text = text };
            Edits.Remove(assetId);

            return report + Suffix(session);
        }
        catch (Exception ex)
        {
            return "save failed: " + ex.Message;
        }
    }

    /// <summary>Saves every edited string, reporting any that could not be written.</summary>
    private static string SaveAll(RomSession session)
    {
        if (_entries is null) return "";

        int saved = 0;
        var failures = new List<string>();

        foreach (int id in Edits.Keys.ToList())
        {
            int at = _entries.FindIndex(e => e.AssetId == id);
            if (at < 0) continue;

            try
            {
                AssetIo.SaveText(ProjectPanel.Folder, id, Edits[id], _entries[at].Text.Length,
                                 session.Rom.Layout.Latin1Documents);
                _entries[at] = _entries[at] with { Text = Edits[id] };
                Edits.Remove(id);
                saved++;
            }
            catch (Exception ex)
            {
                failures.Add($"{id}: {ex.Message}");
            }
        }

        string report = $"saved {saved} string(s) to the project folder";
        if (failures.Count > 0)
            report += $"; {failures.Count} not saved -- " + string.Join("; ", failures.Take(3));

        return report + Suffix(session);
    }

    /// <summary>
    /// What to do next, and one thing that would otherwise look like a failed save: with project
    /// edits switched off, the editor shows the ROM, so the saved text would not appear anywhere.
    /// </summary>
    private static string Suffix(RomSession session)
        => session.Overrides.Enabled
            ? " -- now Build ROM on the Project tab"
            : " -- the editor is showing ROM originals, so tick \"Show project edits\" on the Overrides " +
              "tab to see it; Build ROM uses it either way";
}
