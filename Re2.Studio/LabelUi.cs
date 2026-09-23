using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace Re2.Studio;

/// <summary>
/// The two widgets every list panel needs for labels: a filter box at the top and an editor for
/// whatever is selected.
/// </summary>
public static class LabelUi
{
    /// <summary>Per-panel filter text, keyed by category so each tab keeps its own.</summary>
    private static readonly Dictionary<string, string> Filters = new();

    /// <summary>The label being typed, held until the field loses focus so saving is not per keystroke.</summary>
    private static string _editing = "";
    private static string _editingKey = "";

    /// <summary>Sets a filter without the user typing it, for headless verification.</summary>
    public static void SetFilter(string category, string value) => Filters[category] = value;

    public static string Filter(string category)
        => Filters.TryGetValue(category, out var value) ? value : "";

    /// <summary>Drops a category's filter.</summary>
    public static void ClearFilter(string category) => Filters[category] = "";

    public static string DrawFilter(string category, float width = 220f)
    {
        string filter = Filter(category);

        ImGui.SetNextItemWidth(width);
        if (ImGui.InputTextWithHint($"##filter-{category}", "filter by label or id", ref filter, 128))
            Filters[category] = filter;

        if (filter.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"clear##filter-{category}")) Filters[category] = filter = "";
        }

        return filter;
    }

    /// <summary>Single-asset editor; the common case.</summary>
    public static void DrawEditor(string category, int id, float width = 320f)
        => DrawEditor(category, new[] { id }, width);

    /// <summary>Draws the label field for the selected assets.</summary>
    public static void DrawEditor(string category, IReadOnlyList<int> ids, float width = 320f)
    {
        if (ids.Count == 0) return;

        int id = ids[0];
        // Keyed on the whole selection: changing which assets are selected abandons a half-typed
        // label rather than silently applying it to a different set.
        string key = $"{category}:{string.Join(",", ids)}";

        // Moving to a different asset abandons an uncommitted edit rather than carrying it across.
        if (_editingKey != key)
        {
            _editingKey = key;
            _editing = AssetLabels.Get(category, id);
        }

        ImGui.SetNextItemWidth(width);
        bool entered = ImGui.InputTextWithHint($"##label-{category}", "label (e.g. Ada Wong)", ref _editing, 128,
                                               ImGuiInputTextFlags.EnterReturnsTrue);

        if (entered || ImGui.IsItemDeactivatedAfterEdit())
            foreach (int target in ids)
                AssetLabels.Set(category, target, _editing);

        ImGui.SameLine();
        ImGui.TextDisabled(ids.Count > 1 ? $"label - applies to all {ids.Count} selected" : "label");

        bool anyLabelled = ids.Any(t => AssetLabels.Has(category, t));
        if (anyLabelled)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"clear##label-{category}"))
            {
                _editing = "";
                foreach (int target in ids) AssetLabels.Set(category, target, "");
            }
        }
    }

    /// <summary>
    /// A badge in the corner of a viewport saying that several assets are selected while only one is
    /// being shown.
    /// </summary>
    public static void DrawSelectionBadge(Vector2 rectMin, Vector2 rectMax, int selectedCount, string showing)
    {
        if (selectedCount <= 1) return;

        string text = $"{selectedCount} selected - showing {showing}";
        var size = ImGui.CalcTextSize(text);
        var padding = new Vector2(8, 4);

        var box = size + padding * 2;

        // Top-right of the preview, but kept inside the window: a small preview -- a 64x32 texture,
        // say -- is narrower than the badge, and anchoring to its right edge alone would push the
        // badge off the left of the screen.
        float x = Math.Max(rectMin.X, rectMax.X - box.X - 8);
        x = Math.Min(x, ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - box.X - 8);
        x = Math.Max(x, ImGui.GetWindowPos().X + 8);

        var topRight = new Vector2(x, rectMin.Y + 8);
        var bottomLeft = topRight + box;

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(topRight, bottomLeft, ImGui.GetColorU32(new Vector4(0.10f, 0.12f, 0.16f, 0.88f)), 4f);
        draw.AddRect(topRight, bottomLeft, ImGui.GetColorU32(new Vector4(0.45f, 0.55f, 0.75f, 0.9f)), 4f);
        draw.AddText(topRight + padding, ImGui.GetColorU32(new Vector4(0.85f, 0.90f, 1f, 1f)), text);
    }

    /// <summary>
    /// A row caption with the label appended when there is one, so the list reads as names rather
    /// than numbers without losing the id.
    /// </summary>
    public static string Caption(string category, int id, string baseText)
    {
        string label = AssetLabels.Get(category, id);
        return label.Length == 0 ? baseText : $"{baseText}  -  {label}";
    }

    /// <summary>Reports how many rows a filter is hiding, so an empty-looking list is explicable.</summary>
    public static void DrawFilterSummary(string filter, int shown, int total)
    {
        if (string.IsNullOrWhiteSpace(filter)) return;

        ImGui.SameLine();
        ImGui.TextDisabled(shown == 0
            ? $"nothing matches \"{filter}\" ({total:N0} hidden)"
            : $"{shown:N0} of {total:N0} match \"{filter}\"");
    }
}
