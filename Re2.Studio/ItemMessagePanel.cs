using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Project;

namespace Re2.Studio;

/// <summary>The examine text and inventory prompts, beside the names they belong with.</summary>
public static class ItemMessagePanel
{
    public static void Invalidate() => _records = null;

    /// <summary>Opens a record, for the link from an item's name.</summary>
    public static void Preselect(int record) => _pending = record;

    private static int _pending = -1;

    /// <summary>
    /// What this panel is currently showing, including edits not yet saved, or null before it has
    /// been opened. The names panel previews descriptions from this so the two never disagree.
    /// </summary>
    internal static IReadOnlyList<string>? Live => _edited;

    private static List<string>? _records;    // what is on disk, or the ROM's own
    private static List<string>? _edited;
    private static int _selected;
    private static string _buffer = "";
    private static string _filter = "";
    private static string _status = "";
    private static int _scrollTo = -1;
    private static List<string> _loadedNames = new();

    /// <summary>The names to label records with: the names panel's own, when it has been opened.</summary>
    private static IReadOnlyList<string> Names =>
        ItemNamePanel.Live is { Count: > 0 } live ? live : _loadedNames;

    private static bool Fr => InventoryLanguage.Alternate;

    /// <summary>True while a record has been changed and not yet saved.</summary>
    internal static bool HasUnsavedEdits =>
        _records is not null && _edited is not null && !_records.SequenceEqual(_edited);

    public static void Draw(RomSession session)
    {
        InventoryLanguage.Draw(session);

        if (_records is null)
        {
            _records = ItemTextFile.ExistsIn(ProjectPanel.Folder, InventoryLanguage.FileSuffix) &&
                       ItemTextFile.TryRead(ProjectPanel.Folder, out var saved, out _, InventoryLanguage.FileSuffix)
                ? saved
                : ItemMessages.Read(session.Rom, Fr);

            _edited = new List<string>(_records);
            _selected = Math.Clamp(_selected, 0, _edited.Count - 1);
            _buffer = _edited[_selected];

            // Shown beside each record; read from the project when it has them, as the names panel does.
            _loadedNames = ItemNameFile.ExistsIn(ProjectPanel.Folder, InventoryLanguage.FileSuffix) &&
                     ItemNameFile.TryRead(ProjectPanel.Folder, out var savedNames, out _, InventoryLanguage.FileSuffix)
                ? savedNames
                : ItemNames.Read(session.Rom, Fr);
        }

        var records = _edited!;

        if (_pending >= 0 && _pending < records.Count)
        {
            _selected = _pending;
            _buffer = records[_selected];
            _scrollTo = _selected;
            _filter = "";                 // a filter would otherwise hide what was just asked for
        }
        _pending = -1;

        int used = ItemMessages.Measure(records, InventoryLanguage.Charset);
        bool encodable = used >= 0;
        int spare = InventoryLanguage.MessagesCapacity - used;
        int changed = records.Where((t, i) => t != _records[i]).Count();

        if (encodable)
        {
            ImGui.Text($"{records.Count} records   {used:N0} of {InventoryLanguage.MessagesCapacity:N0} bytes   " +
                       $"{changed} edited");
            ImGui.SameLine();
            if (spare >= 0) ImGui.TextDisabled($"({spare:N0} spare)");
            else ImGui.TextColored(Red, $"({-spare:N0} over -- shorten something)");

            ImGui.ProgressBar(Math.Clamp(used / (float)InventoryLanguage.MessagesCapacity, 0, 1),
                              new Vector2(-1, 6), "");
        }
        else ImGui.TextColored(Red, "One of the records uses a character the game cannot draw.");

        ImGui.SetNextItemWidth(260);
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
                : !encodable ? "One of the records cannot be written in the game's characters."
                : spare < 0 ? "The text does not fit. Shorten something first."
                : $"Writes {ItemTextFile.NameOf(InventoryLanguage.FileSuffix)} in the project folder.");

        ImGui.SameLine();
        ImGui.BeginDisabled(changed == 0);
        if (ImGui.Button("Revert all"))
        {
            _edited = new List<string>(_records);
            _buffer = _edited[_selected];
        }
        ImGui.EndDisabled();

        if (_status.Length > 0) AssetIoUi.DrawStatus(_status);
        if (!ready) ImGui.TextDisabled("To save, first Extract a project on the Project tab.");

        ImGui.Separator();
        ImGui.BeginChild("recordlist", new Vector2(430, 0), ImGuiChildFlags.Border);

        for (int i = 0; i < records.Count; i++)
        {
            string preview = Preview(records[i]);

            if (_filter.Length > 0 &&
                !records[i].Contains(_filter, StringComparison.OrdinalIgnoreCase) &&
                !i.ToString().Contains(_filter))
                continue;

            bool dirty = records[i] != _records[i];

            if (_scrollTo == i) { ImGui.SetScrollHereY(0.5f); _scrollTo = -1; }

            // The item this record describes, so the list can be read against the names.
            int item = ItemMessages.ItemForRecord(i);
            var names = Names;
            string owner = item >= 0 && item < names.Count && names[item].Length > 0
                ? names[item] + " -- "
                : "";

            if (ImGui.Selectable($"{(dirty ? "*" : " ")}{i,3}  {owner}{preview}##r{i}", i == _selected))
            {
                _selected = i;
                _buffer = records[i];
            }
        }

        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("recordedit", new Vector2(0, 0), ImGuiChildFlags.None);
        DrawEditor(records);
        ImGui.EndChild();
    }

    private static void DrawEditor(List<string> records)
    {
        int cost = ItemText.MeasureOrMinusOne(_buffer, InventoryLanguage.Charset);
        bool dirty = records[_selected] != _records![_selected];

        int owner = ItemMessages.ItemForRecord(_selected);
        var names = Names;
        bool named = owner >= 0 && owner < names.Count && names[owner].Length > 0;

        ImGui.Text($"record {_selected}");

        if (named)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{names[owner]} (item {owner})##toname"))
            {
                ItemNamePanel.Preselect(owner);
                TextTab.Show(TextTab.Names);
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show this item on the Item names tab.");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled("a prompt, not an item description");
        }

        ImGui.SameLine();
        if (cost < 0) ImGui.TextColored(Red, "cannot be written as it stands");
        else if (dirty) ImGui.TextColored(Amber, $"{cost} bytes, was {ItemText.MeasureOrMinusOne(_records[_selected], InventoryLanguage.Charset)}");
        else ImGui.TextDisabled($"{cost} bytes");

        // Room for the legend and the buttons below it.
        if (ImGui.InputTextMultiline("##record", ref _buffer, 2048,
                                     new Vector2(-1, ImGui.GetContentRegionAvail().Y - 96)))
            records[_selected] = _buffer;

        if (cost < 0)
        {
            ItemText.TryEncode(_buffer, out _, out string why, InventoryLanguage.Charset);
            ImGui.TextColored(Red, why);
        }

        ImGui.BeginDisabled(!dirty);
        if (ImGui.Button("Revert this record"))
        {
            records[_selected] = _records[_selected];
            _buffer = records[_selected];
        }
        ImGui.EndDisabled();

        ImGui.Separator();

        // Wrapped rather than plain: the legend is two long sentences and ran off the right edge.
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped("A line break is a newline. <FD> starts a new page of the message, " +
                          "<FE> ends it, and <XX> is any other code whose meaning is not known -- " +
                          "all three type back exactly as they read. The game can draw " +
                          InventoryLanguage.Characters + "; anything else is refused " +
                          "rather than silently dropped.");
        ImGui.PopStyleColor();
    }

    /// <summary>The first line of a record, which is what makes it recognisable in a list.</summary>
    private static string Preview(string record)
    {
        string first = record.Replace("\n", " ").Trim();
        return first.Length > 44 ? first[..44] : first;
    }

    private static readonly Vector4 Red = new(0.92f, 0.45f, 0.40f, 1);
    private static readonly Vector4 Amber = new(0.93f, 0.76f, 0.36f, 1);

    private static void Save()
    {
        try
        {
            ItemTextFile.Write(ProjectPanel.Folder, _edited!, InventoryLanguage.FileSuffix);
            _records = new List<string>(_edited!);
            _status = $"saved {ItemTextFile.NameOf(InventoryLanguage.FileSuffix)} -- now Build ROM on the Project tab";
        }
        catch (Exception ex)
        {
            _status = "save failed: " + ex.Message;
        }
    }
}
