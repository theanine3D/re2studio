using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using Re2.Core.Assets;
using Re2.Core.Video;

namespace Re2.Studio;

/// <summary>
/// The FMV tab: the game's prerendered movies, which are plain MPEG-1 video elementary streams.
/// </summary>
public static class FmvPanel
{
    private static readonly Selection _selection = new();

    private static int _selected;
    private static string _exportStatus = "";
    private static string _importStatus = "";
    private static string _exportFolder = "";
    private static string _importPath = "";

    // ---- playback ----

    private static FmvFrames.Movie _movie = FmvFrames.Movie.Empty;
    private static Task<(FmvFrames.Movie Movie, string Error)>? _decoding;
    private static int _decodingAsset = -1;
    private static int _loadedAsset = -1;
    private static string _playError = "";

    private static GlTexture? _texture;
    private static int _shownFrame = -1;
    private static double _position;
    private static bool _playing, _loop = true;

    /// <summary>How much bigger than its own pixels the picture is drawn.</summary>
    private const int Zoom = 2;

    /// <summary>Rates the preview will play at, the game's own first.</summary>
    private static readonly (string Label, double Fps)[] Rates =
    {
        ("15 fps (in game)", FmvStream.GameFrameRate), ("30 fps (as stored)", 0),
        ("25 fps", 25), ("24 fps", 24), ("20 fps", 20), ("12 fps", 12),
    };

    /// <summary>Defaults to the first entry: the rate the game plays at.</summary>
    private static int _rate;

    private static double PlaybackFps
        => Rates[_rate].Fps > 0 ? Rates[_rate].Fps
                                : _movie.FramesPerSecond > 0 ? _movie.FramesPerSecond : 30;

    /// <summary>How long the movie runs at the rate the preview is using.</summary>
    private static double PlaybackSeconds
        => _movie.Frames.Count == 0 ? 0 : _movie.Frames.Count / PlaybackFps;

    /// <summary>Picks a movie, for the headless capture and for jumps from elsewhere.</summary>
    public static void Preselect(int index) => _pendingSelection = index;

    private static int _pendingSelection = -1;

    /// <summary>Drops a decoded movie, for when the ROM or an override changes underneath.</summary>
    public static void Invalidate()
    {
        _movie = FmvFrames.Movie.Empty;
        _loadedAsset = -1;
        _shownFrame = -1;
        _playing = false;
        _position = 0;
    }

    public static void Draw(RomSession session, TextureCache cache)
    {
        var movies = session.Movies;

        if (movies.Count == 0)
        {
            ImGui.TextWrapped("This ROM has no movies.");
            return;
        }

        if (_exportFolder.Length == 0)
            _exportFolder = Path.Combine(Path.GetDirectoryName(session.Path) ?? ".", "fmv");

        // The set at a glance, including every picture size in it -- which is the only way to tell
        // whether they are all one shape without opening each in turn.
        var sizes = movies.Select(m => $"{m.Width}x{m.Height}").Distinct().ToList();

        ImGui.Text($"{movies.Count:N0} movies   " +
                   $"{TimeSpan.FromSeconds(movies.Sum(m => m.Seconds)):hh\\:mm\\:ss} stored / " +
                   $"{TimeSpan.FromSeconds(movies.Sum(m => m.GameSeconds)):hh\\:mm\\:ss} in game   " +
                   $"{movies.Sum(m => (long)m.StoredSize) / 1024 / 1024:N0} MB   " +
                   $"{string.Join(", ", sizes)} MPEG-1");

        // Said once, at the top: not having ffmpeg costs both the preview and every import that is
        // not already an .m2v, and finding that out by clicking Import is too late.
        if (!Ffmpeg.Available)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                              "ffmpeg was not found. Movies cannot be played or converted without it " +
                              "-- install it and put it on your PATH. Exporting, and importing an " +
                              ".m2v, work regardless.");

        string filter = LabelUi.DrawFilter(AssetLabels.Movie);

        _selection.Constrain(movies.Count);

        if (_pendingSelection >= 0 && _pendingSelection < movies.Count)
        {
            _selection.Set(_pendingSelection);
            _selection.ScrollToRow = _pendingSelection;
            _pendingSelection = -1;
        }

        var visible = new List<int>(movies.Count);
        for (int i = 0; i < movies.Count; i++)
            if (Matches(movies[i], filter)) visible.Add(i);

        _selection.HandleArrowKeys(visible);

        DrawList(movies, visible);

        LabelUi.DrawFilterSummary(filter, visible.Count, movies.Count);

        // Something is always selected, so the right-hand side never appears or disappears.
        if (_selection.Count == 0 && visible.Count > 0) _selection.Set(visible[0]);
        _selected = Math.Max(0, _selection.Primary);
        if (_selected >= movies.Count) _selected = 0;

        ImGui.SameLine();
        ImGui.BeginChild("fmv-view", Vector2.Zero, ImGuiChildFlags.Border);

        var movie = movies[_selected];

        // Both lengths, because they differ: the stream stores 30 fps and the game plays 15, so
        // what the file says and what a player sits through are not the same number.
        ImGui.Text($"{(movie.Name.Length > 0 ? movie.Name : $"asset {movie.AssetId}")}   " +
                   $"{movie.Width}x{movie.Height}   {movie.Pictures:N0} frames   " +
                   $"{movie.Seconds:0.00}s stored / {movie.GameSeconds:0.00}s in game");
        ImGui.TextDisabled($"asset {movie.AssetId}   {movie.StoredSize:N0} bytes   " +
                           $"{movie.DeclaredBitRate / 1000:N0} kbit/s   " +
                           $"{movie.FramesPerSecond:0.##} fps   {movie.Gops:N0} groups");

        LabelUi.DrawEditor(AssetLabels.Movie, _selection.ToAssetIds(i => movies[i].AssetId));

        DrawPlayer(session, cache, movie);
        DrawIo(session, movie);

        ImGui.EndChild();
    }

    private static bool Matches(FmvStream movie, string filter)
        => AssetLabels.Matches(AssetLabels.Movie, movie.AssetId, filter)
           || (filter.Length > 0 && movie.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static void DrawList(IReadOnlyList<FmvStream> movies, List<int> visible)
    {
        ImGui.BeginChild("fmv-list", new Vector2(300, 0), ImGuiChildFlags.Border);

        foreach (int i in visible)
        {
            var movie = movies[i];
            string fallback = movie.Name.Length > 0
                ? Path.GetFileNameWithoutExtension(movie.Name)
                : $"fmv{movie.AssetId}";

            string caption = LabelUi.Caption(AssetLabels.Movie, movie.AssetId, fallback);

            if (ImGui.Selectable($"{caption}##fmv{i}", _selection.Contains(i)))
                _selection.Click(i, visible);

            // A keyboard move can land on a row that is scrolled out of sight.
            if (_selection.ScrollToRow >= 0 && i == _selection.Primary)
            {
                ImGui.SetScrollHereY(0.5f);
                _selection.ScrollToRow = -1;
            }
        }

        ImGui.EndChild();
    }

    /// <summary>The picture and its transport.</summary>
    private static void DrawPlayer(RomSession session, TextureCache cache, FmvStream movie)
    {
        if (_decoding is null && _loadedAsset != movie.AssetId && _decodingAsset != movie.AssetId)
        {
            _decodingAsset = movie.AssetId;
            _playError = "";

            var data = session.MovieData(movie);
            _decoding = Task.Run(() =>
            {
                var decoded = FmvFrames.Decode(data, out string error);
                return (decoded, error);
            });
        }

        if (_decoding is { IsCompleted: true })
        {
            (_movie, _playError) = _decoding.Result;
            _loadedAsset = _decodingAsset;
            _decoding = null;
            _decodingAsset = -1;

            _position = 0;
            _shownFrame = -1;
            _playing = _movie.Frames.Count > 0;

            // A movie of different dimensions needs a different texture.
            if (_texture is not null && (_texture.Width != _movie.Width || _texture.Height != _movie.Height))
            {
                _texture.Dispose();
                _texture = null;
            }
        }

        // Drawn at the stream's own shape.
        var area = new Vector2(movie.Width * Zoom, movie.Height * Zoom);

        if (_decoding is not null || _movie.Frames.Count == 0)
        {
            var origin = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(origin, origin + area,
                ImGui.GetColorU32(new Vector4(0.09f, 0.09f, 0.11f, 1f)));

            ImGui.GetWindowDrawList().AddText(origin + new Vector2(8, 8),
                ImGui.GetColorU32(ImGuiCol.TextDisabled),
                _decoding is not null ? "decoding..."
                                      : _playError.Length > 0 ? _playError : "nothing decoded");

            ImGui.Dummy(area);

            ImGui.BeginDisabled();
            DrawTransport(0, 1);
            ImGui.EndDisabled();
            return;
        }

        if (_playing)
        {
            _position += ImGui.GetIO().DeltaTime;

            if (_position >= PlaybackSeconds)
            {
                if (_loop) _position = 0;
                else { _position = PlaybackSeconds; _playing = false; }
            }
        }

        int frame = Math.Clamp((int)(_position * PlaybackFps), 0, _movie.Frames.Count - 1);

        if (frame != _shownFrame)
        {
            _texture ??= cache.Create(_movie.Frames[frame], _movie.Width, _movie.Height, smooth: true);
            _texture.Update(_movie.Frames[frame]);
            _shownFrame = frame;
        }

        ImGui.Image((IntPtr)_texture!.Handle, new Vector2(_movie.Width * Zoom, _movie.Height * Zoom));
        LabelUi.DrawSelectionBadge(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                                   _selection.Count, movie.Name);

        DrawTransport(frame, _movie.Frames.Count);
    }

    private static void DrawTransport(int frame, int frames)
    {
        if (ImGui.Button(_playing ? "Pause" : "Play", new Vector2(90, 0))) _playing = !_playing;

        ImGui.SameLine();
        if (ImGui.Button("Restart")) { _position = 0; _playing = true; }

        ImGui.SameLine();
        ImGui.Checkbox("Loop", ref _loop);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);

        float at = (float)_position;
        if (ImGui.SliderFloat("##scrub", ref at, 0, (float)Math.Max(0.001, PlaybackSeconds), "%.2fs"))
        {
            _position = at;
            _playing = false;
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"frame {frame + 1:N0} / {frames:N0}");

        ImGui.SetNextItemWidth(130);
        int rate = _rate;
        if (ImGui.Combo("##rate", ref rate, string.Join('\0', Rates.Select(r => r.Label)) + "\0"))
        {
            _rate = rate;
            _position = 0;
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"preview speed -- {PlaybackFps:0.##} fps, {PlaybackSeconds:0.00}s. " +
                           "The game plays these at 15 fps, half the stored 30. Exports keep 30.");
    }

    private static void DrawIo(RomSession session, FmvStream movie)
    {
        ImGui.Separator();
        if (!ImGui.CollapsingHeader("Export / import")) return;

        ImGui.SetNextItemWidth(460);
        ImGui.InputText("export folder", ref _exportFolder, 512);

        if (ImGui.Button("Export as .m2v"))
            _exportStatus = PanelIo.Run(() => AssetIo.ExportMovie(session, _exportFolder, movie));

        ImGui.SameLine();
        if (ImGui.Button("Export selected"))
            _exportStatus = PanelIo.Run(() =>
            {
                var chosen = _selection.Count > 0
                    ? _selection.ToAssetIds(i => i).Select(i => session.Movies[i])
                    : new[] { movie };

                int done = chosen.Count(m => AssetIo.ExportMovie(session, _exportFolder, m).Length > 0);
                return $"exported {done} movie(s) to {_exportFolder}";
            });

        ImGui.TextWrapped(
            "An exported .m2v is a standard MPEG-1 video file -- any player opens it, and it comes " +
            "straight out of the ROM with no re-encoding.");

        AssetIoUi.DrawStatus(_exportStatus);

        ImGui.Separator();
        bool ready = AssetIoUi.DrawProjectNote();

        ImGui.SetNextItemWidth(460);
        ImGui.InputText("video to import", ref _importPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Choose..."))
        {
            string? picked = NativeDialogs.OpenFile("Choose a video",
                                                    "Video\0*.mp4;*.m2v;*.mov;*.avi;*.mkv\0" +
                                                    "MPEG-1 stream\0*.m2v\0MP4\0*.mp4\0All files\0*.*\0");
            if (picked is not null) _importPath = picked;
        }

        ImGui.BeginDisabled(!ready || _importPath.Length == 0);
        if (ImGui.Button($"Import into {(movie.Name.Length > 0 ? movie.Name : $"movie {movie.AssetId}")}"))
            _importStatus = PanelIo.Run(() =>
                AssetIo.ImportMovie(session, ProjectPanel.Folder, movie, _importPath));
        ImGui.EndDisabled();

        ImGui.TextWrapped(
            $"An .m2v goes in as it is. Any other format is converted to {movie.Width}x{movie.Height} " +
            "at 30 fps, the only shape the game plays. Anything longer than the slot is cut to its " +
            "length first, and only then is the bit rate chosen: the editor searches for the best " +
            "picture that still fits the space the original movie occupied.");

        AssetIoUi.DrawStatus(_importStatus);
    }
}
