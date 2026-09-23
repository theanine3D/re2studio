using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Re2.Core.Assets;

namespace Re2.Studio;

/// <summary>The dialogue tab: the 588 speech clips in the voice bank.</summary>
public static class VoicePanel
{
    private static readonly Selection _selection = new();

    private static int _selected = -1;
    private static string _status = "";
    private static string _exportStatus = "";
    private static string _importStatus = "";
    private static string _exportFolder = "";
    private static string _importPath = "";
    private static bool _loop;

    private static byte[]? _wav;
    private static short[]? _pcm;

    /// <summary>Deferred so a click on the arrows is applied once, at the top of the next frame.</summary>
    private static int _pendingStep;
    private static int _pendingSelection = -1;
    private static int _scrollToRow = -1;

    public static void Preselect(int index) => _pendingSelection = index;

    /// <summary>
    /// Seeds a multi-selection, for the headless capture that cannot Ctrl+click its way to one.
    /// </summary>
    public static void PreselectRange(int start, int count) => _pendingRange = (start, count);

    private static (int Start, int Count)? _pendingRange;

    /// <summary>Drops decoded audio, for when the ROM or an override changes underneath.</summary>
    public static void Invalidate()
    {
        _pcm = null;
        _wav = null;

        // What went stale is the audio, not the selection, so the selection is left alone and the clip
        // is simply decoded again.
        _reload = true;
    }

    private static bool _reload;

    public static void Draw(RomSession session)
    {
        var clips = session.Voices;

        if (clips.Count == 0)
        {
            ImGui.TextWrapped("This ROM has no voice bank.");
            return;
        }

        if (_exportFolder.Length == 0)
            _exportFolder = Path.Combine(Path.GetDirectoryName(session.Path) ?? ".", "voice");

        ImGui.Text($"{clips.Count:N0} clips   {TimeSpan.FromSeconds(clips.Sum(c => c.Seconds)):hh\\:mm\\:ss}   " +
                   $"{clips.Count(c => c.SampleRate == VoiceBank.LowRate)} at 8 kHz");
        ImGui.Separator();

        string filter = LabelUi.DrawFilter(AssetLabels.Voice);

        var visible = new List<int>(clips.Count);
        for (int i = 0; i < clips.Count; i++)
            if (AssetLabels.Matches(AssetLabels.Voice, clips[i].Index, filter))
                visible.Add(i);

        if (_pendingSelection >= 0 && _pendingSelection < clips.Count)
        {
            Select(session, _pendingSelection);
            _pendingSelection = -1;
        }

        // After the single selection, not before: a headless run always asks for a clip (clip 0 by
        // default) and only sometimes asks for a range, so the range is the more specific request
        // and has to be the one that survives.
        if (_pendingRange is { } range)
        {
            _selection.SetRange(range.Start, range.Count, clips.Count);
            _selected = -1;
            ShowPrimary(session);
            _pendingRange = null;
        }

        if (_reload)
        {
            _reload = false;
            if (_selected >= 0 && _selected < clips.Count) Load(session);
        }

        if (_pendingStep != 0)
        {
            Step(session, visible, _pendingStep);
            _pendingStep = 0;
        }

        if (_selection.HandleArrowKeys(visible))
        {
            _selected = _selection.Primary;
            _scrollToRow = _selection.ScrollToRow;
            _selection.ScrollToRow = -1;
            Load(session);
        }

        if (_selected >= 0 && _selected < clips.Count)
        {
            var clip = clips[_selected];

            DrawTransport(session, clip);
            DrawWaveform(session, clip);

            LabelUi.DrawSelectionBadge(_waveformMin, _waveformMax, _selection.Count, $"clip {clip.Index}");
            LabelUi.DrawEditor(AssetLabels.Voice, _selection.ToAssetIds(i => clips[i].Index));

            DrawIo(session, clip, visible);
        }
        else ImGui.TextDisabled("Select a clip to play it.");

        AssetIoUi.DrawStatus(_status);
        ImGui.Separator();

        DrawList(session, clips, visible, filter);
    }

    private static void DrawTransport(RomSession session, VoiceClip clip)
    {
        ImGui.Text($"clip {clip.Index}   {clip.SampleRate} Hz   {clip.Seconds:0.00}s   " +
                   $"{clip.BlockCount:N0} blocks   {clip.StoredSize:N0} bytes encoded");

        if (!AudioPlayer.Supported)
        {
            ImGui.TextDisabled(AudioPlayer.Unavailable + " Export instead.");
            return;
        }

        if (ImGui.ArrowButton("##previous-voice", ImGuiDir.Left)) _pendingStep = -1;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play the previous clip");

        ImGui.SameLine();

        bool playingThis = AudioPlayer.IsPlaying && AudioPlayer.PlayingIndex == _selected;

        if (ImGui.Button(playingThis ? "Stop" : "Play", new Vector2(90, 0)))
        {
            if (playingThis) AudioPlayer.Stop();
            else Play(session, clip);
        }

        ImGui.SameLine();
        if (ImGui.ArrowButton("##next-voice", ImGuiDir.Right)) _pendingStep = 1;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play the next clip");

        ImGui.SameLine();
        if (ImGui.Checkbox("Loop", ref _loop) && playingThis) Play(session, clip);

        ImGui.SameLine();
        if (playingThis) ImGui.Text($"{AudioPlayer.Position:0.00} / {clip.Seconds:0.00}s");
        else ImGui.TextDisabled($"{clip.Seconds:0.00}s at {clip.SampleRate} Hz");
    }

    private static void DrawIo(RomSession session, VoiceClip clip, IReadOnlyList<int> visible)
    {
        if (!ImGui.CollapsingHeader("Export / import")) return;

        ImGui.SetNextItemWidth(560);
        ImGui.InputText("export folder", ref _exportFolder, 512);

        if (ImGui.Button($"Export clip {clip.Index} as WAV"))
            _exportStatus = PanelIo.Run(() => AssetIo.ExportVoiceWav(session, _exportFolder, clip));

        ImGui.SameLine();
        if (ImGui.Button("Export selected"))
            _exportStatus = PanelIo.Run(() =>
            {
                var chosen = _selection.Count > 0
                    ? _selection.ToAssetIds(i => i).Select(i => session.Voices[i])
                    : new[] { clip };

                int done = chosen.Count(c => AssetIo.ExportVoiceWav(session, _exportFolder, c).Length > 0);
                return $"exported {done} clip(s) to {_exportFolder}";
            });

        ImGui.SameLine();
        if (ImGui.Button("Export encoded"))
            _exportStatus = PanelIo.Run(() => AssetIo.ExportVoiceRaw(session, _exportFolder, clip));

        AssetIoUi.DrawStatus(_exportStatus);

        ImGui.Separator();
        bool ready = AssetIoUi.DrawProjectNote();

        ImGui.SetNextItemWidth(420);
        ImGui.InputText("clip to import", ref _importPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose..."))
        {
            string? picked = NativeDialogs.OpenFile("Choose a WAV or an encoded clip",
                                                    "Audio\0*.wav;*.mort\0WAV\0*.wav\0" +
                                                    "MORT clip\0*.mort\0All files\0*.*\0");
            if (picked is not null) _importPath = picked;
        }

        bool encoded = _importPath.EndsWith(".mort", StringComparison.OrdinalIgnoreCase);

        ImGui.BeginDisabled(!ready || _importPath.Length == 0);
        if (ImGui.Button($"Import into clip {clip.Index}"))
            _importStatus = PanelIo.Run(() => encoded
                ? AssetIo.ImportVoiceRaw(session, ProjectPanel.Folder, clip, _importPath)
                : AssetIo.ImportVoiceWav(session, ProjectPanel.Folder, clip, _importPath));
        ImGui.EndDisabled();

        ImGui.TextWrapped(
            "A WAV is encoded into the game's own speech codec. That codec is lossy and heavily so " +
            "-- about 1.6 bits a sample -- so expect the result to sound like the game's dialogue " +
            "does rather than like the file you put in. A .mort file is taken as already encoded and " +
            "stored as-is, which is how a line is moved or copied between slots without losing a " +
            "generation.");

        ImGui.TextWrapped(
            "A clip always keeps its slot. Audio shorter than the slot is padded with silence to the " +
            "original length, and audio longer than it is trimmed -- dialogue is timed to the action " +
            "in its scene. Nothing else in the bank moves. If the encode will not fit the slot's " +
            "bytes, the quietest parts are dropped to silence until it does, which is noted in the " +
            "log.");

        AssetIoUi.DrawStatus(_importStatus);
        ImGui.Separator();
    }

    private static void DrawList(RomSession session, IReadOnlyList<VoiceClip> clips,
                                 List<int> visible, string filter)
    {
        LabelUi.DrawFilterSummary(filter, visible.Count, clips.Count);
        _selection.Constrain(clips.Count);

        if (!ImGui.BeginTable("voices", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                           ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var column in new[] { "clip", "label", "length", "rate", "encoded" })
            ImGui.TableSetupColumn(column);
        ImGui.TableHeadersRow();

        if (_scrollToRow >= 0)
        {
            int row = visible.IndexOf(_scrollToRow);
            if (row >= 0)
                ImGui.SetScrollY(Math.Max(0, row * ImGui.GetTextLineHeightWithSpacing()
                                             - ImGui.GetWindowHeight() / 2));
            _scrollToRow = -1;
        }

        var clipper = new ImGuiListClipper();
        ImGuiListClipperPtr clipperPtr;
        unsafe { clipperPtr = new ImGuiListClipperPtr(&clipper); }
        clipperPtr.Begin(visible.Count);

        while (clipperPtr.Step())
        {
            for (int row = clipperPtr.DisplayStart; row < clipperPtr.DisplayEnd; row++)
            {
                int i = visible[row];
                var clip = clips[i];

                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                if (ImGui.Selectable($"{clip.Index}##voice{i}", _selection.Contains(i),
                                     ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selection.Click(i, visible);
                    ShowPrimary(session);
                }

                ImGui.TableNextColumn(); ImGui.TextUnformatted(AssetLabels.Get(AssetLabels.Voice, clip.Index));
                ImGui.TableNextColumn(); ImGui.Text($"{clip.Seconds:0.00}s");
                ImGui.TableNextColumn(); ImGui.Text($"{clip.SampleRate} Hz");
                ImGui.TableNextColumn(); ImGui.Text($"{clip.StoredSize:N0}");
            }
        }

        clipperPtr.End();
        ImGui.EndTable();
    }

    /// <summary>Replaces the selection with one clip, for the arrows and for external callers.</summary>
    private static void Select(RomSession session, int index)
    {
        _selection.Set(index);
        _selected = index;
        Load(session);
    }

    /// <summary>
    /// Follows the selection's primary clip after a click, which is what the transport and the waveform
    /// show while the rest of the selection comes along for labelling.
    /// </summary>
    private static void ShowPrimary(RomSession session)
    {
        int primary = _selection.Primary;
        if (primary == _selected) return;

        _selected = primary;
        Load(session);
    }

    /// <summary>Decoding is deferred to here so merely scrolling the list costs nothing.</summary>
    private static void Load(RomSession session)
    {
        _pcm = null;
        _wav = null;

        if (_selected < 0 || _selected >= session.Voices.Count) return;

        try { _pcm = session.DecodeVoice(session.Voices[_selected]); }
        catch (Exception ex) { _status = "Could not decode this clip: " + ex.Message; }
    }

    private static void Play(RomSession session, VoiceClip clip)
    {
        _pcm ??= session.DecodeVoice(clip);
        _wav ??= Re2.Core.Export.WavCodec.Write(_pcm, clip.SampleRate);

        if (!AudioPlayer.Play(_wav, _selected, clip.Seconds, _loop))
            _status = "Could not play this clip; the audio device refused it.";
    }

    /// <summary>Moves to the next or previous visible clip and plays it, as the arrows do.</summary>
    private static void Step(RomSession session, List<int> visible, int direction)
    {
        if (visible.Count == 0) return;

        int at = visible.IndexOf(_selected);
        int next = at < 0 ? 0 : Math.Clamp(at + direction, 0, visible.Count - 1);

        Select(session, visible[next]);
        _scrollToRow = _selected;

        if (AudioPlayer.Supported) Play(session, session.Voices[_selected]);
    }

    private static Vector2 _waveformMin, _waveformMax;

    private static void DrawWaveform(RomSession session, VoiceClip clip)
    {
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 120);
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        _waveformMin = origin;
        _waveformMax = origin + size;

        uint background = ImGui.GetColorU32(new Vector4(0.09f, 0.09f, 0.11f, 1f));
        uint axis = ImGui.GetColorU32(new Vector4(0.35f, 0.35f, 0.40f, 1f));
        uint wave = ImGui.GetColorU32(new Vector4(0.45f, 0.70f, 0.95f, 1f));

        draw.AddRectFilled(origin, origin + size, background);
        float mid = origin.Y + size.Y / 2;
        draw.AddLine(new Vector2(origin.X, mid), new Vector2(origin.X + size.X, mid), axis);

        var pcm = _pcm;
        if (pcm is { Length: > 0 })
        {
            int columns = Math.Max(1, (int)size.X);
            for (int x = 0; x < columns; x++)
            {
                int from = (int)((long)x * pcm.Length / columns);
                int to = Math.Max(from + 1, (int)((long)(x + 1) * pcm.Length / columns));

                short low = short.MaxValue, high = short.MinValue;
                for (int i = from; i < to && i < pcm.Length; i++)
                {
                    if (pcm[i] < low) low = pcm[i];
                    if (pcm[i] > high) high = pcm[i];
                }

                draw.AddLine(new Vector2(origin.X + x, mid - high / 32768f * (size.Y / 2)),
                             new Vector2(origin.X + x, mid - low / 32768f * (size.Y / 2)), wave);
            }
        }
        else ImGui.GetWindowDrawList().AddText(origin + new Vector2(8, 8),
                                               ImGui.GetColorU32(ImGuiCol.TextDisabled), "decoding...");

        if (AudioPlayer.IsPlaying && AudioPlayer.PlayingIndex == _selected && clip.Seconds > 0)
        {
            float x = origin.X + size.X * (float)Math.Clamp(AudioPlayer.Position / clip.Seconds, 0, 1);
            draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y),
                         ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 0.9f)));
        }

        ImGui.Dummy(size);
    }
}
