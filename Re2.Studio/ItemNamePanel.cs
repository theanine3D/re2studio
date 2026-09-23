using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Project;

namespace Re2.Studio;

/// <summary>The inventory item names -- what the game prints when an item is highlighted.</summary>
public static class ItemNamePanel
{
    /// <summary>Drops the loaded names so they are read again, after a project scan or a revert.</summary>
    public static void Invalidate() => _names = null;

    /// <summary>Scrolls to an item, for the link back from its description.</summary>
    public static void Preselect(int itemId) => _pending = itemId;

    private static int _pending = -1;

    /// <summary>
    /// What this panel is currently showing, including edits not yet saved, or null before it has
    /// been opened. The item text panel labels its records from this.
    /// </summary>
    internal static IReadOnlyList<string>? Live => _edited;

    private static List<string>? _names;      // what is on disk, or the ROM's own
    private static List<string>? _edited;     // what is on screen
    private static string _filter = "";
    private static string _status = "";
    private static int _scrollTo = -1;
    private static List<string> _loadedRecords = new();

    /// <summary>The descriptions to preview: the item text panel's own, when it has been opened.</summary>
    private static IReadOnlyList<string> Descriptions =>
        ItemMessagePanel.Live is { Count: > 0 } live ? live : _loadedRecords;

    public static void Draw(RomSession session)
    {
        if (_names is null)
        {
            // The project file is the thing a build reads, so it is the baseline whenever it exists.
            _names = ItemNameFile.ExistsIn(ProjectPanel.Folder) &&
                     ItemNameFile.TryRead(ProjectPanel.Folder, out var saved, out _)
                ? saved
                : ItemNames.Read(session.Rom);

            _edited = new List<string>(_names);

            // The descriptions, so each name can show the text that goes with it.
            _loadedRecords = ItemTextFile.ExistsIn(ProjectPanel.Folder) &&
                       ItemTextFile.TryRead(ProjectPanel.Folder, out var savedText, out _)
                ? savedText
                : ItemMessages.Read(session.Rom);
        }

        if (_pending >= 0)
        {
            _scrollTo = _pending;
            _filter = "";                 // a filter would otherwise hide what was just asked for
            _pending = -1;
        }

        var names = _edited!;

        int used = ItemNames.Measure(names);
        bool encodable = used >= 0;
        int spare = ItemNames.Capacity - used;
        int changed = names.Where((t, i) => t != _names[i]).Count();

        if (encodable)
        {
            ImGui.Text($"{names.Count} items   {used:N0} of {ItemNames.Capacity:N0} bytes   " +
                       $"{changed} edited");
            ImGui.SameLine();

            if (spare >= 0) ImGui.TextDisabled($"({spare:N0} spare)");
            else ImGui.TextColored(Red, $"({-spare:N0} over -- shorten something)");

            // A bar, because "235 spare" means much less than seeing how little is left.
            ImGui.ProgressBar(Math.Clamp(used / (float)ItemNames.Capacity, 0, 1),
                              new Vector2(-1, 6), "");
        }
        else
        {
            ImGui.TextColored(Red, "One of the names uses a character the game cannot draw.");
        }

        ImGui.TextDisabled("Names share one fixed block, so a longer name has to be paid for by a " +
                           "shorter one. Repeated names are stored once.");

        ImGui.SetNextItemWidth(300);
        ImGui.InputText("filter", ref _filter, 128);

        bool ready = AssetIo.HasProject(ProjectPanel.Folder);

        ImGui.SameLine();
        ImGui.BeginDisabled(!ready || changed == 0 || !encodable || spare < 0);
        if (ImGui.Button("Save")) Save();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(
                !ready ? "Extract a project on the Project tab first -- that is where it is saved."
                : changed == 0 ? "Nothing to save: these match the project."
                : !encodable ? "One of the names cannot be written in the game's characters."
                : spare < 0 ? "The names do not fit. Shorten some of them first."
                : $"Writes {ItemNameFile.Name} in the project folder.");

        ImGui.SameLine();
        ImGui.BeginDisabled(changed == 0);
        if (ImGui.Button("Revert all")) _edited = new List<string>(_names);
        ImGui.EndDisabled();

        if (_status.Length > 0) AssetIoUi.DrawStatus(_status);

        if (!ready)
            ImGui.TextDisabled("To save, first Extract a project on the Project tab.");

        ImGui.Separator();

        // Wide enough for the description preview beside each name; the sidebar keeps its own width.
        float listWidth = Math.Max(460, ImGui.GetContentRegionAvail().X - 340);
        ImGui.BeginChild("names", new Vector2(listWidth, 0), ImGuiChildFlags.Border);

        for (int i = 0; i < names.Count; i++)
        {
            if (_filter.Length > 0 &&
                !names[i].Contains(_filter, StringComparison.OrdinalIgnoreCase) &&
                !i.ToString().Contains(_filter))
                continue;

            bool dirty = names[i] != _names[i];
            int cost = ItemText.MeasureOrMinusOne(names[i]);

            if (_scrollTo == i) { ImGui.SetScrollHereY(0.5f); _scrollTo = -1; }

            ImGui.TextDisabled($"{i,3}");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(260);
            string value = names[i];
            if (ImGui.InputText($"##name{i}", ref value, 48)) names[i] = value;

            ImGui.SameLine();

            // Item 0 is "no item" and is meant to be empty, so it is not worth flagging.
            if (cost < 0) ImGui.TextColored(Red, "cannot be written");
            else if (dirty) ImGui.TextColored(Amber, $"{cost}B was \"{Short(_names[i])}\"");
            else ImGui.TextDisabled($"{cost}B");

            DrawTextLink(i);
        }

        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("sidebar", new Vector2(0, 0), ImGuiChildFlags.Border);
        DrawSidebar(names, used, spare, encodable);
        ImGui.EndChild();
    }

    /// <summary>
    /// What is worth knowing while editing against a fixed budget: where the room is going, and
    /// which characters can be typed at all.
    /// </summary>
    private static void DrawSidebar(List<string> names, int used, int spare, bool encodable)
    {
        ImGui.TextDisabled("BUDGET");

        if (encodable)
        {
            ImGui.Text($"{used:N0} of {ItemNames.Capacity:N0} bytes");
            if (spare >= 0) ImGui.Text($"{spare:N0} spare");
            else ImGui.TextColored(Red, $"{-spare:N0} over");
        }
        else ImGui.TextColored(Red, "one name cannot be written");

        ImGui.TextWrapped("The block ends where the game's own lookup table begins, and that address " +
                          "is compiled into the code, so it cannot grow. Identical names are stored " +
                          "once, which is why the total is less than the names added up.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("LONGEST NAMES");
        ImGui.TextWrapped("Somewhere to find room when the budget is tight.");
        ImGui.Spacing();

        var longest = names
            .Select((name, i) => (Name: name, Index: i, Cost: ItemText.MeasureOrMinusOne(name)))
            .Where(x => x.Cost > 0)
            .GroupBy(x => x.Name)
            .Select(g => (g.Key, Count: g.Count(), g.First().Cost))
            .OrderByDescending(x => x.Cost)
            .Take(10);

        foreach (var (name, count, cost) in longest)
            ImGui.Text($"{cost,3}B  {name}" + (count > 1 ? $"  (x{count}, stored once)" : ""));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("CHARACTERS");
        ImGui.TextWrapped("A-Z, a-z, 0-9, space, and . , ! ? / ' -");
        ImGui.TextWrapped("Anything else the game has no glyph for is refused rather than " +
                          "silently dropped.");
        ImGui.Spacing();
        ImGui.TextWrapped("A code with no known meaning shows as <XX> and can be typed back the " +
                          "same way. <EE>6 is the ampersand in \"Bomb & Det.\".");
    }

    /// <summary>The description this item carries, as a button that opens it.</summary>
    private static void DrawTextLink(int itemId)
    {
        int record = ItemMessages.RecordForItem(itemId);
        var records = Descriptions;
        if (record < 0 || record >= records.Count) return;

        ImGui.SameLine();

        string preview = records[record].Replace('\n', ' ').Trim();
        if (preview.Length > 46) preview = preview[..46] + "...";

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
        if (ImGui.SmallButton($"{preview}##link{itemId}"))
        {
            ItemMessagePanel.Preselect(record);
            TextTab.Show(TextTab.Examine);
        }
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Record {record}. Click to edit it on the Item text tab.");
    }

    private static string Short(string text) => text.Length > 18 ? text[..18] + "..." : text;

    private static readonly Vector4 Red = new(0.92f, 0.45f, 0.40f, 1);
    private static readonly Vector4 Amber = new(0.93f, 0.76f, 0.36f, 1);

    private static void Save()
    {
        try
        {
            ItemNameFile.Write(ProjectPanel.Folder, _edited!);

            // What was just written is the new baseline, so the edited count and markers change with
            // the file rather than a project rescan later.
            _names = new List<string>(_edited!);
            _status = $"saved {ItemNameFile.Name} -- now Build ROM on the Project tab";
        }
        catch (Exception ex)
        {
            _status = "save failed: " + ex.Message;
        }
    }
}
