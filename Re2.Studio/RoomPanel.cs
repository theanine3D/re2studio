using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Re2.Core.Assets;
using Re2.Core.Rom;
using Re2.Core.Formats;

namespace Re2.Studio;

/// <summary>The rooms, and the backgrounds each one's camera angles draw.</summary>
public static class RoomPanel
{
    private static readonly Selection _selection = new();

    private static int _selected = -1;
    private static string _filter = "";
    private static int _pendingSelection = -1;

    /// <summary>Whether thumbnails show the foreground picked out.</summary>
    public static bool ShowForegrounds;

    public static void Preselect(int flatIndex) => _pendingSelection = flatIndex;

    public static void Invalidate()
    {
        if (_selected >= 0) _pendingSelection = _selected;
        _selected = -1;
    }

    public static void Draw(RomSession session, TextureCache cache)
    {
        var rooms = session.Rooms;
        _masks = session.RoomMasks;

        if (rooms.Count == 0)
        {
            ImGui.TextWrapped("This ROM has no room table.");
            return;
        }

        int views = rooms.Sum(r => r.ViewCount);
        ImGui.Text($"{rooms.Count} rooms in {RoomTable.StageCount} stages   {views:N0} camera views");
        ImGui.TextDisabled("Rooms are addressed as stage and room, shown in hex -- the same pair the " +
                           "Room Modifier cheat writes. Background numbers match the Backgrounds tab.");
        ImGui.Separator();

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##room-filter", "filter by stage-room, in hex", ref _filter, 64);

        ImGui.SameLine();
        ImGui.Checkbox("Show foregrounds", ref ShowForegrounds);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tints the parts of each view that draw in front of the player.");

        var visible = new List<int>(rooms.Count);
        for (int i = 0; i < rooms.Count; i++)
        {
            var room = rooms[i];

            // Hex is what the list shows and what the cheat wants, but a decimal room number is still
            // a reasonable thing to type, so both match.
            if (_filter.Length == 0
                || Address(room).Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || $"{room.Stage}-{room.Room}".Contains(_filter, StringComparison.OrdinalIgnoreCase))
            {
                visible.Add(i);
            }
        }

        if (_pendingSelection >= 0 && _pendingSelection < rooms.Count)
        {
            _selected = _pendingSelection;
            _selection.Set(_selected);
            _pendingSelection = -1;
        }

        if (_selection.HandleArrowKeys(visible)) _selected = _selection.Primary;

        DrawList(rooms, visible);

        ImGui.SameLine();
        DrawViews(session, cache, rooms);
    }

    private static void DrawList(IReadOnlyList<RoomEntry> rooms, List<int> visible)
    {
        ImGui.BeginChild("room-list", new Vector2(240, 0), ImGuiChildFlags.Border);

        int stage = -1;
        foreach (int i in visible)
        {
            var room = rooms[i];

            // A heading per stage, since the room numbering restarts in each.
            if (room.Stage != stage)
            {
                stage = room.Stage;
                if (i != visible[0]) ImGui.Spacing();
                ImGui.TextDisabled($"stage {stage:X2}");
            }

            if (_selection.ScrollToRow >= 0 && i == _selection.Primary)
            {
                ImGui.SetScrollHereY(0.5f);
                _selection.ScrollToRow = -1;
            }

            int masked = MaskedViews(rooms, i);

            string caption = room.ViewCount > 0
                ? $"{Address(room)}   {room.ViewCount} views" + (masked > 0 ? $"   {masked} fg" : "")
                : $"{Address(room)}   (no backgrounds)";

            if (ImGui.Selectable($"{caption}##room{i}", _selection.Contains(i)))
            {
                _selection.Click(i, visible);
                if (_selection.Primary >= 0) _selected = _selection.Primary;
            }
        }

        ImGui.EndChild();
    }

    /// <summary>How a room is written: two hex digits for the stage and two for the room.</summary>
    private static string Address(RoomEntry room) => $"{room.Stage:X2}-{room.Room:X2}";

    /// <summary>How many of a room's cameras have something drawing in front of the player.</summary>
    private static int MaskedViews(IReadOnlyList<RoomEntry> rooms, int index)
    {
        var masks = _masks;
        if (masks is null || index >= masks.Count) return 0;

        int count = 0;
        foreach (int id in masks[index]) if (id >= 0) count++;
        return count;
    }

    private static IReadOnlyList<int[]>? _masks;

    private static void DrawViews(RomSession session, TextureCache cache, IReadOnlyList<RoomEntry> rooms)
    {
        ImGui.BeginChild("room-views", new Vector2(0, 0), ImGuiChildFlags.Border);

        if (_selected < 0 || _selected >= rooms.Count)
        {
            ImGui.TextDisabled("Select a room to see the backgrounds it uses.");
            ImGui.EndChild();
            return;
        }

        var room = rooms[_selected];
        if (room.FlatIndex != _entranceRoom) { _entranceRoom = room.FlatIndex; _entrance = 0; _copied = ""; }

        ImGui.Text($"stage {room.Stage:X2}, room {room.Room:X2}");
        ImGui.TextDisabled($"stage {room.Stage} room {room.Room} in decimal");
        ImGui.TextDisabled($"flat room index {room.FlatIndex} of {rooms.Count}");

        if (room.ViewCount == 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("This room draws no backgrounds. Five of the rooms are like this -- " +
                              "slots the game never displays.");
            ImGui.EndChild();
            return;
        }

        DrawEntrances(session, room);

        ImGui.Separator();
        ImGui.Text($"{room.ViewCount} camera views");
        ImGui.Spacing();

        // Thumbnails wrap to the width available, each labelled with the number the Backgrounds tab
        // shows, so a view can be found there.
        const float Thumbnail = 128f;
        float width = ImGui.GetContentRegionAvail().X;
        float used = 0;

        for (int camera = 0; camera < room.ViewCount; camera++)
        {
            int assetId = room.BackgroundAssetIds[camera];
            int index = session.BackgroundIndexOfAsset(assetId);

            if (used + Thumbnail > width && used > 0) { used = 0; }
            else if (camera > 0) ImGui.SameLine();

            ImGui.BeginGroup();

            bool hasForeground = session.LoadMask(room.FlatIndex, camera) is not null;

            var texture = index >= 0 ? Thumb(session, cache, index, room.FlatIndex, camera) : null;
            var size = new Vector2(Thumbnail, Thumbnail * 224 / 320f);

            if (texture is not null) ImGui.Image((IntPtr)texture.Handle, size);
            else ImGui.Dummy(size);

            // Double-clicking a view opens it in the Backgrounds tab, which is where it can be
            // exported or replaced.
            if (index >= 0 && ImGui.IsItemHovered())
            {
                var mask = session.LoadMask(room.FlatIndex, camera);
                string foreground = mask is null
                    ? "no foreground"
                    : $"{mask.Pieces.Count} foreground pieces, {mask.CoveredPixels:N0} pixels";

                ImGui.SetTooltip($"bg{index:D4} -- {foreground}" + Environment.NewLine +
                                 "double-click to open in Backgrounds");
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    BackgroundPanel.Preselect(index);
                    Program.ShowTab("Backgrounds");
                }
            }

            ImGui.TextUnformatted(index >= 0 ? $"bg{index:D4}" : $"asset {assetId}");
            ImGui.TextDisabled(hasForeground ? $"cam {camera}  fg" : $"cam {camera}");

            ImGui.EndGroup();
            used += Thumbnail + ImGui.GetStyle().ItemSpacing.X;
        }

        ImGui.EndChild();
    }

    /// <summary>The ways into this room, and the cheat codes that use one.</summary>
    private static void DrawEntrances(RomSession session, RoomEntry room)
    {
        var entrances = session.Entrances;
        if (room.FlatIndex >= entrances.Count) return;

        var doors = entrances[room.FlatIndex];

        ImGui.Separator();

        if (doors.Count == 0)
        {
            ImGui.TextDisabled("No door leads here, so there is no entry point to warp to. " +
                               "Seven rooms are like this -- mostly the endings.");
            return;
        }

        if (!ImGui.CollapsingHeader($"Entry points and cheat codes ({doors.Count})")) return;

        ImGui.TextDisabled("Warping needs a position as well as a room number. Pick a way in:");
        ImGui.Spacing();

        for (int i = 0; i < doors.Count; i++)
        {
            var door = doors[i];
            var from = session.Rooms[door.FromRoom];

            string caption = $"from {from.Stage:X2}-{from.Room:X2}   " +
                             $"({door.DestX}, {door.DestY}, {door.DestZ})   cam {door.DestCamera}" +
                             (door.IsLocked ? "   locked" : "");

            if (ImGui.RadioButton($"{caption}##entrance{i}", _entrance == i)) { _entrance = i; _copied = ""; }
        }

        int pick = Math.Clamp(_entrance, 0, doors.Count - 1);

        ImGui.Spacing();

        // A cheat is the whole block, not a line: an emulator takes all of a cheat's lines as one
        // entry, so clicking anywhere in a block copies every line of it.
        int delta = Re2Version.Detect(session.Rom).MainOverlayDelta;

        foreach (var (title, codes) in GameSharkCodes.Groups(doors[pick], delta))
        {
            ImGui.BeginGroup();

            ImGui.TextDisabled(title);
            foreach (string code in codes) ImGui.TextUnformatted("   " + code);

            ImGui.EndGroup();

            bool hovered = ImGui.IsItemHovered();

            // An outline rather than a fill, so the codes stay readable under the cursor.
            if (hovered)
            {
                ImGui.GetWindowDrawList().AddRect(
                    ImGui.GetItemRectMin() - new Vector2(4, 2),
                    ImGui.GetItemRectMax() + new Vector2(4, 2),
                    ImGui.GetColorU32(ImGuiCol.ButtonHovered), 3f);

                ImGui.SetTooltip($"click to copy this cheat -- all {codes.Count} lines");

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    ImGui.SetClipboardText(string.Join(Environment.NewLine, codes));
                    _copied = title;
                }
            }

            if (_copied == title)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"copied {codes.Count} lines");
            }

            ImGui.Spacing();
        }

        if (ImGui.Button("Copy both")) { ImGui.SetClipboardText(GameSharkCodes.Describe(doors[pick], delta)); _copied = ""; }

        ImGui.SameLine();
        ImGui.TextDisabled("Prefer the cheat-button form.");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The 88/89 codes write only while the cheat button is held, so they place " +
                             "the player once and then leave the game alone." + Environment.NewLine +
                             "The fallback form writes the player's live position every frame. It pins the " +
                             "facing angle while on, and switching it off leaves the player invisible and " +
                             "unable to interact -- use it only if your emulator has no cheat button.");
    }

    /// <summary>The code most recently copied, so the click has some visible acknowledgement.</summary>
    private static string _copied = "";

    private static int _entrance;
    private static int _entranceRoom = -1;

    /// <summary>
    /// A background as a GPU texture, decoded on demand and kept by the shared cache.
    /// </summary>
    private static GlTexture? Thumb(RomSession session, TextureCache cache, int backgroundIndex,
                                    int roomIndex, int camera)
    {
        try
        {
            var mask = ShowForegrounds ? session.LoadMask(roomIndex, camera) : null;

            // Tinted and untinted are different images, so they are cached under different keys --
            // sharing one would leave whichever was decoded first on screen after the toggle.
            int key = (mask is null ? BackgroundCacheKey : ForegroundCacheKey) + backgroundIndex;

            return cache.Get(key, () =>
            {
                var jpeg = session.GetBackgroundJpeg(backgroundIndex);

                // The index is passed through: some backgrounds have swapped chroma planes and the
                // codec corrects them by number.
                var (pixels, width, height) = BackgroundCodec.JpegToRgba(jpeg);

                if (mask is null) return (pixels, width, height);

                return (MaskRenderer.Highlight(mask, pixels, width, height), mask.Width, mask.Height);
            });
        }
        catch (Exception) { return null; }
    }

    private const int BackgroundCacheKey = 1_000_000;
    private const int ForegroundCacheKey = 2_000_000;
}
