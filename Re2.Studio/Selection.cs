using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;

namespace Re2.Studio;

/// <summary>
/// A multi-selection over one asset list, with the click behaviour people already expect from a file
/// manager: plain click replaces the selection, Ctrl+click toggles one entry, Shift+click extends from
/// the last plain click.
/// </summary>
public sealed class Selection
{
    private readonly SortedSet<int> _indices = new();

    /// <summary>Where a Shift+click measures from: the last plain or Ctrl click.</summary>
    private int _anchor = -1;

    public int Count => _indices.Count;

    /// <summary>First selected item in list order, or -1 when nothing is selected.</summary>
    public int Primary => _indices.Count == 0 ? -1 : _indices.Min;

    public bool IsMultiple => _indices.Count > 1;

    public bool Contains(int index) => _indices.Contains(index);

    public IReadOnlyList<int> Indices => _indices.ToList();

    /// <summary>Replaces the selection with a single item, as an external caller would.</summary>
    public void Set(int index)
    {
        _indices.Clear();
        if (index >= 0) _indices.Add(index);
        _anchor = index;
    }

    /// <summary>Selects a contiguous run starting at <paramref name="start"/>.</summary>
    public void SetRange(int start, int count, int limit)
    {
        _indices.Clear();
        for (int i = start; i < start + count && i < limit; i++)
            if (i >= 0) _indices.Add(i);
        _anchor = start;
    }

    public void Clear()
    {
        _indices.Clear();
        _anchor = -1;
    }

    /// <summary>
    /// Applies a click, reading the modifier keys itself so every panel behaves the same.
    /// </summary>
    public void Click(int index, IReadOnlyList<int> visibleOrder)
    {
        bool ctrl = CtrlHeld;
        bool shift = ShiftHeld;

        if (shift && _anchor >= 0)
        {
            int from = PositionOf(visibleOrder, _anchor);
            int to = PositionOf(visibleOrder, index);

            // If the anchor has been filtered away there is no range to speak of; fall back to
            // treating this as a plain click rather than selecting something arbitrary.
            if (from < 0 || to < 0) { Set(index); return; }

            if (from > to) (from, to) = (to, from);

            if (!ctrl) _indices.Clear();
            for (int i = from; i <= to; i++) _indices.Add(visibleOrder[i]);
            return;
        }

        if (ctrl)
        {
            if (!_indices.Remove(index)) _indices.Add(index);
            _anchor = index;
            return;
        }

        Set(index);
    }

    /// <summary>
    /// Modifier state, read as individual keys rather than through ImGui's aggregate io.KeyCtrl /
    /// io.KeyShift flags.
    /// </summary>
    public static bool CtrlHeld
        => ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);

    private static bool ShiftHeld
        => ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);

    /// <summary>
    /// Set by <see cref="HandleArrowKeys"/> to the row in the visible order that should be brought into
    /// view, or -1.
    /// </summary>
    public int ScrollToRow { get; set; } = -1;

    /// <summary>
    /// Moves the selection with the up and down arrow keys, and reports whether it changed so the
    /// caller can reload whatever it shows.
    /// </summary>
    public bool HandleArrowKeys(IReadOnlyList<int> visibleOrder, int stride = 1, bool horizontal = false)
    {
        if (visibleOrder.Count == 0) return false;

        // Never steal the arrows from a text field -- the filter box, the label editor and the text
        // tab's body all need them for the caret.
        if (ImGui.GetIO().WantTextInput) return false;

        bool up = ImGui.IsKeyPressed(ImGuiKey.UpArrow, repeat: true);
        bool down = ImGui.IsKeyPressed(ImGuiKey.DownArrow, repeat: true);

        // Left and right are only meaningful where a row holds more than one item -- the texture grid.
        bool left = horizontal && ImGui.IsKeyPressed(ImGuiKey.LeftArrow, repeat: true);
        bool right = horizontal && ImGui.IsKeyPressed(ImGuiKey.RightArrow, repeat: true);

        int pressed = (up ? 1 : 0) + (down ? 1 : 0) + (left ? 1 : 0) + (right ? 1 : 0);
        if (pressed != 1) return false;                     // nothing, or an ambiguous combination

        // Up and down cross a whole row; left and right move one item, which lets them run off the
        // end of a row onto the next, the way reading order does.
        int step = down ? stride : up ? -stride : right ? 1 : -1;
        bool forward = step > 0;

        int target;
        if (_indices.Count == 0)
        {
            // Nothing selected: come in from the end the arrow points away from.
            target = forward ? 0 : visibleOrder.Count - 1;
        }
        else
        {
            // The edge of the selection the arrow moves away from.
            int first = int.MaxValue, last = -1;
            foreach (int index in _indices)
            {
                int position = PositionOf(visibleOrder, index);
                if (position < 0) continue;
                if (position < first) first = position;
                if (position > last) last = position;
            }

            // Every selected item is hidden by the filter: treat it as nothing selected.
            if (last < 0) target = forward ? 0 : visibleOrder.Count - 1;
            else target = forward ? last + step : first + step;
        }

        target = Math.Clamp(target, 0, visibleOrder.Count - 1);

        int chosen = visibleOrder[target];
        if (_indices.Count == 1 && _indices.Min == chosen) return false;

        Set(chosen);
        ScrollToRow = target;
        return true;
    }

    /// <summary>IReadOnlyList has no IndexOf of its own, and Linq would allocate per click.</summary>
    private static int PositionOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return -1;
    }

    /// <summary>
    /// Drops anything no longer in range, for when a different ROM is loaded and the lists change
    /// length underneath the selection.
    /// </summary>
    public void Constrain(int count)
    {
        _indices.RemoveWhere(i => i < 0 || i >= count);
        if (_anchor >= count) _anchor = -1;
    }

    /// <summary>Maps the selection to asset ids through whatever the panel keys its labels by.</summary>
    public List<int> ToAssetIds(Func<int, int> idOf)
        => _indices.Select(idOf).Distinct().ToList();
}
