using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using Re2.Core.Assets;
using Re2.Core.Rom;
using Re2.Core.Formats;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace Re2.Studio;

/// <summary>RE2 Studio: a single-window tool for browsing and editing the ROM's assets.</summary>
public static class Program
{
    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static ImGuiController _imgui = null!;
    private static IInputContext _input = null!;

    private static RomSession? _session;
    private static TextureCache _cache = null!;
    private static ModelViewport _viewport = null!;

    private static string _status = "Open a ROM to begin.";
    private static string _romPath = "";
    private static Settings _settings = new();

    /// <summary>When set, render one character to a PNG and exit. Used to verify the GL path.</summary>
    private static string? _screenshot;

    /// <summary>
    /// When set, capture the whole window to a PNG and exit, with the Characters tab forced open.
    /// </summary>
    private static string? _windowShot;
    /// <summary>The tab a capture asks for with --tab.</summary>
    private static string _forceTab = "";
    private static int _highlightTexture = -1;
    private static int _jumpToBackground = -1;
    private static int _forceSound;
    private static bool _startPlayback;
    private static int _pinClip = -1;
    private static int _pinFrame = -1;
    private static string? _forceFilter;
    private static string? _setLabel;

    /// <summary>"start:count" for --select, which seeds a multi-selection a headless run cannot click.</summary>
    private static (int Start, int Count)? _selectRange;
    private static int _screenshotCharacter;

    /// <summary>Select by mesh asset id instead of list position, which is how models are named.</summary>
    private static int _screenshotMesh = -1;
    private static int _framesRendered;

    /// <summary>Frames to wait before --window-shot fires, so a script can drive the UI first.</summary>
    private static int _shotDelayFrames = 5;

    /// <summary>Turns the joint markers on for a headless capture.</summary>
    private static bool _showJoints;

    public static int Main(string[] args)
    {
        // Built as a windowed application so double-clicking it does not flash up a console.
        NativeDialogs.AttachToParentConsole();

        // Silk.NET finds its windowing and input backends by scanning assemblies on disk, which a
        // single-file build has none of -- it fails with "Couldn't find a suitable window platform".
        Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
        Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

        _settings = Settings.Load();
        AssetLabels.Load();


        if (args.Length > 0 && !args[0].StartsWith("--")) _romPath = args[0];

        // Switches that take a value, so the last argument is never one of these.
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--screenshot") _screenshot = args[i + 1];
            if (args[i] == "--window-shot") _windowShot = args[i + 1];
            if (args[i] == "--tab") _forceTab = TopLevelOf(args[i + 1]);
            if (args[i] == "--sound") int.TryParse(args[i + 1], out _forceSound);
            if (args[i] == "--character") int.TryParse(args[i + 1], out _screenshotCharacter);
            if (args[i] == "--mesh") int.TryParse(args[i + 1], out _screenshotMesh);
            if (args[i] == "--highlight") int.TryParse(args[i + 1], out _highlightTexture);
            if (args[i] == "--jump-bg") int.TryParse(args[i + 1], out _jumpToBackground);
            if (args[i] == "--menu") { int.TryParse(args[i + 1], out int m); MenuPanel.Pending = m; }
            if (args[i] == "--texture") { int.TryParse(args[i + 1], out int t); TexturePanel.PreselectAsset(t); }
            if (args[i] == "--text") { int.TryParse(args[i + 1], out int x); TextPanel.Preselect(x); }
            if (args[i] == "--clip") int.TryParse(args[i + 1], out _pinClip);
            if (args[i] == "--frame") int.TryParse(args[i + 1], out _pinFrame);
            if (args[i] == "--filter") _forceFilter = args[i + 1];
            if (args[i] == "--set-label") _setLabel = args[i + 1];
            if (args[i] == "--shot-delay") int.TryParse(args[i + 1], out _shotDelayFrames);
            if (args[i] == "--modifier-probe-out") ModifierProbe.ReportPath = args[i + 1];
            if (args[i] == "--select")
            {
                var parts = args[i + 1].Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int count))
                    _selectRange = (start, count);
            }
        }

        // Switches that take no value need their own pass, or one written last would be missed.
        foreach (string arg in args)
        {
            if (arg == "--play") _startPlayback = true;
            if (arg == "--foregrounds") RoomPanel.ShowForegrounds = true;
            if (arg == "--modifier-probe") ModifierProbe.Enabled = true;
            if (arg == "--rest") _characterPanel.ForceRestPose = true;
            if (arg == "--joints") _showJoints = true;
            if (arg == "--all-parts") ModelViewport.DrawPartsWithoutJoints = true;
            if (arg == "--skip-stub") ModelViewport.SkipStubJointRotation = true;
        }

        if (_setLabel is not null)
        {
            int eq = _setLabel.IndexOf('=');
            int colon = _setLabel.IndexOf(':');
            if (eq > 0 && colon > 0 && colon < eq)
            {
                string category = _setLabel[..colon];
                if (int.TryParse(_setLabel[(colon + 1)..eq], out int id))
                {
                    AssetLabels.Set(category, id, _setLabel[(eq + 1)..]);
                    Console.WriteLine($"set {category}:{id} -> \"{_setLabel[(eq + 1)..]}\" in {AssetLabels.Path}");
                }
            }
            return 0;
        }

        // Nothing on the command line: reopen whatever was last used, which is the normal case for
        // someone who just double-clicked the icon.
        if (_romPath.Length == 0 && _settings.LastRom is { Length: > 0 } remembered && File.Exists(remembered))
            _romPath = remembered;

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1400, 900),
            Title = "RE2 Studio",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3))
        };

        // On Linux prefer X11 (XWayland) when a display is available: the desktop then draws a proper
        // title bar. Native Wayland depends on libdecor, which is missing on some systems and, in
        // old versions, leaves the window unmapped (WSLg). GLFW_PLATFORM = GLFW_PLATFORM_X11.
        if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Silk.NET.GLFW.Glfw.GetApi().InitHint((Silk.NET.GLFW.InitHint)0x00050003, 0x00060004);

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += size => _gl.Viewport(size);
        _window.Closing += OnClosing;
        _window.Run();
        return 0;
    }

    private static void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        _imgui = new ImGuiController(_gl, _window, _input);
        RedirectImGuiSettings();
        _cache = new TextureCache(_gl);
        _viewport = new ModelViewport(_gl) { ShowJoints = _showJoints };

        if (!string.IsNullOrWhiteSpace(_romPath) && File.Exists(_romPath)) OpenRom(_romPath);

        if (_windowShot is not null)
        {
            // Whichever browser the capture is aimed at, so an item model can be render-checked the
            // same way a character can.
            var browser = _forceTab switch { "Items" => _itemPanel, "Scenery" => _sceneryPanel, _ => _characterPanel };

            if (_screenshotMesh >= 0) browser.PreselectMesh(_screenshotMesh);
            else browser.Preselect(_screenshotCharacter);
            if (_pinClip >= 0 || _pinFrame >= 0) browser.PinFrame(_pinClip, _pinFrame);
            if (_highlightTexture >= 0) browser.PreselectTexture(_highlightTexture);
            SoundPanel.Preselect(_forceSound);
            VoicePanel.Preselect(_forceSound);
            FmvPanel.Preselect(_forceSound);
            RoomPanel.Preselect(_forceSound);

            // Exercises the jump the Rooms tab makes on a double-click, so the capture can show the
            // Backgrounds tab actually landing on the requested image.
            if (_jumpToBackground >= 0)
            {
                BackgroundPanel.Preselect(_jumpToBackground);
                ShowTab("Backgrounds");
            }

            if (_forceFilter is not null)
            {
                string category = _forceTab switch
                {
                    "Backgrounds" => AssetLabels.Background,
                    "Textures" => AssetLabels.Texture,
                    "Sounds" => AssetLabels.Sound,
                    "Assets" => AssetLabels.Asset,
                    "Text" => AssetLabels.Text,
                    "FMV" => AssetLabels.Movie,
                    "Dialogue" => AssetLabels.Voice,
                    _ => AssetLabels.Model
                };
                LabelUi.SetFilter(category, _forceFilter);
            }
            if (_selectRange is { } sel)
            {
                switch (_forceTab)
                {
                    case "Textures": TexturePanel.PreselectRange(sel.Start, sel.Count); break;
                    case "Sounds": SoundPanel.PreselectRange(sel.Start, sel.Count); break;
                    case "Dialogue": VoicePanel.PreselectRange(sel.Start, sel.Count); break;
                    default: BackgroundPanel.PreselectRange(sel.Start, sel.Count); break;
                }
            }
            if (_startPlayback) SoundPanel.RequestPlay();
        }
    }

    /// <summary>Points ImGui's layout file at the user's settings folder.</summary>
    private static unsafe void RedirectImGuiSettings()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            string path = Path.Combine(Settings.Folder, "layout.ini");
            // ImGui keeps the pointer, so this allocation deliberately lives for the process.
            ImGui.GetIO().NativePtr->IniFilename =
                (byte*)System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(path);
        }
        catch (Exception) { /* the default location still works */ }
    }

    /// <summary>Throws away decoded and uploaded data when the choice of ROM-or-edit changes.</summary>
    private static void ApplyOverrideChanges()
    {
        if (_session is null) return;

        // Something in the project folder was written since the last look: pick it up without the
        // user having to press Rescan, or restart the editor.
        if (_session.Overrides.NeedsRescan)
            _session.Overrides.Scan(ProjectPanel.Folder, _session.Rom);

        if (_session.Overrides.Version == _overrideVersion) return;

        _overrideVersion = _session.Overrides.Version;
        _session.InvalidateCaches();

        // The panels keep their own decoded copies too, and those are not reached by the session's
        // caches. Missing these is what made the Overrides switches look like they did nothing.
        _characterPanel.Invalidate();
        _itemPanel.Invalidate();
        _sceneryPanel.Invalidate();
        RoomPanel.Invalidate();
        SoundPanel.Invalidate();
        VoicePanel.Invalidate();
        FmvPanel.Invalidate();
        TextPanel.Invalidate();
        ItemNamePanel.Invalidate();
        ItemMessagePanel.Invalidate();
        MenuPanel.Invalidate();
        IconPanel.Invalidate();

        _cache.Dispose();
        _cache = new TextureCache(_gl);
    }

    private static int _overrideVersion = -1;

    private static void OpenRom(string path)
    {
        try
        {
            _session?.Dispose();
            _cache.Dispose();
            _cache = new TextureCache(_gl);
            _session = new RomSession(path);
            _romPath = path;
            // Plain ASCII: the em dash that used to sit here was stored mis-encoded and drew as "â€”".
            _status = $"{Path.GetFileName(path)} -- Resident Evil 2 {Re2Version.Detect(_session.Rom)} -- " +
                      $"{_session.Assets.Entries.Count:N0} assets, " +
                      $"{_session.Backgrounds.Count:N0} backgrounds";
            _settings.RememberRom(path);

            // Before any tab is drawn, so the import tabs know about an existing extract straight away.
            ProjectPanel.Initialise(path, _settings);

            // And so an edit made in a previous session is visible immediately rather than after a
            // trip to the Overrides tab.
            _session.Overrides.Scan(ProjectPanel.Folder, _session.Rom);

            // An import writes into the project folder; this is how the editor hears about it
            // directly, instead of waiting to spot its own writes through the folder watcher.
            AssetIo.ProjectAssetWritten -= OnProjectAssetWritten;
            AssetIo.ProjectAssetWritten += OnProjectAssetWritten;

            // Deliberately a value the session's version can never hold, rather than whatever it reads
            // right now.
            _overrideVersion = -1;
            // The build is named too: the two USA releases keep their tables in different places, so
            // which one is open decides what the editor is reading.
            _window.Title = $"RE2 Studio - {Path.GetFileName(path)} [{Re2Version.Detect(_session.Rom)}]";
        }
        catch (Exception ex)
        {
            _session = null;
            _status = "Failed to open: " + ex.Message;
        }
    }

    /// <summary>
    /// The Characters tab and the Items tab are the same browser over two different tables.
    /// </summary>
    private static readonly ModelBrowserPanel _characterPanel =
        new("char", session => session.Characters);

    private static readonly ModelBrowserPanel _itemPanel =
        new("item", session => session.Items, showSlot: true);

    private static readonly ModelBrowserPanel _sceneryPanel =
        new("scenery", session => session.Scenery);

    private static void OnProjectAssetWritten() => _session?.Overrides.NoteEdited();

    private static void OnClosing()
    {
        AudioPlayer.Stop();
        _viewport?.Dispose();
        _cache?.Dispose();
        _session?.Dispose();
        _imgui?.Dispose();
    }

    /// <summary>
    /// Tells ImGui which modifier keys are held, which the Silk.NET backend never does -- and without
    /// which no text box in the editor has working Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+A or Ctrl+Z.
    /// </summary>
    private static void SendModifierKeys()
    {
        if (_input.Keyboards.Count == 0) return;

        var keyboard = _input.Keyboards[0];
        var io = ImGui.GetIO();

        io.AddKeyEvent(ImGuiKey.ModCtrl,
                       keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight));
        io.AddKeyEvent(ImGuiKey.ModShift,
                       keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight));
        io.AddKeyEvent(ImGuiKey.ModAlt,
                       keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight));
        io.AddKeyEvent(ImGuiKey.ModSuper,
                       keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight));
    }

    private static void OnRender(double delta)
    {
        ApplyOverrideChanges();

        // Before Update, because Update is where NewFrame runs and turns these into io.KeyMods.
        SendModifierKeys();
        _imgui.Update((float)delta);
        ModifierProbe.Sample(_input);
        _cache.BeginFrame();
        _gl.ClearColor(0.09f, 0.09f, 0.11f, 1f);
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.Begin("root", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus);

        DrawToolbar();

        if (_session is null)
        {
            DrawWelcome();
        }
        else if (ImGui.BeginTabBar("tabs"))
        {
            // Taken once, before any tab is drawn.
            _activeRequest = _requestedTab;
            _requestedTab = "";

            if (BeginTab("Backgrounds")) { BackgroundPanel.Draw(_session, _cache, ref _status); ImGui.EndTabItem(); }
            if (BeginTab("Rooms")) { RoomPanel.Draw(_session, _cache); ImGui.EndTabItem(); }
            if (BeginTab("Textures")) { TexturePanel.Draw(_session, _cache); ImGui.EndTabItem(); }
            if (BeginTab("Menus")) { MenusTab.Draw(_session, _cache); ImGui.EndTabItem(); }
            if (BeginTab("Characters")) { _characterPanel.Draw(_session, _cache, _viewport, (float)delta); ImGui.EndTabItem(); }
            if (BeginTab("Items")) { _itemPanel.Draw(_session, _cache, _viewport, (float)delta); ImGui.EndTabItem(); }
            if (BeginTab("Scenery")) { _sceneryPanel.Draw(_session, _cache, _viewport, (float)delta); ImGui.EndTabItem(); }
            if (BeginTab("Sounds")) { SoundPanel.Draw(_session); ImGui.EndTabItem(); }
            if (BeginTab("Dialogue")) { VoicePanel.Draw(_session); ImGui.EndTabItem(); }
            if (BeginTab("FMV")) { FmvPanel.Draw(_session, _cache); ImGui.EndTabItem(); }
            if (BeginTab("Text")) { TextTab.Draw(_session); ImGui.EndTabItem(); }
            if (BeginTab("Overrides")) { OverridePanel.Draw(_session); ImGui.EndTabItem(); }
            if (BeginTab("Assets")) { AssetPanel.Draw(_session); ImGui.EndTabItem(); }
            if (BeginTab("Project")) { ProjectPanel.Draw(_session); ImGui.EndTabItem(); }
            if (BeginTab("About")) { AboutPanel.Draw(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        ImGui.End();

        // Belt and braces: a panel that renders offscreen must put the viewport back, but if one
        // ever forgets, ImGui would draw the entire interface into whatever rectangle was left.
        var framebuffer = _window.FramebufferSize;
        _gl.Viewport(0, 0, (uint)framebuffer.X, (uint)framebuffer.Y);

        _imgui.Render();

        _framesRendered++;

        if (_screenshot is not null && _session is not null && _framesRendered > 2)
        {
            CaptureViewport(_screenshot);
            _window.Close();
        }

        // A few more frames than the character capture: the tab has to be selected, drawn, and its
        // offscreen pass run at least once before the state it leaves behind could show up.
        if (_windowShot is not null && _session is not null && _framesRendered > _shotDelayFrames)
        {
            CaptureWindow(_windowShot);
            _window.Close();
        }
    }

    /// <summary>Renders one character offscreen and writes the framebuffer to a PNG.</summary>
    private static unsafe void CaptureViewport(string path)
    {
        var characters = _session!.Characters;
        if (characters.Count == 0) return;

        int pick = _screenshotMesh >= 0
            ? characters.ToList().FindIndex(c => c.MeshAssetId == _screenshotMesh)
            : _screenshotCharacter;
        if (pick < 0) { Console.WriteLine($"no character uses mesh {_screenshotMesh}"); return; }

        var character = characters[Math.Clamp(pick, 0, characters.Count - 1)];
        var mesh = _session.LoadMesh(character.MeshAssetId);
        if (mesh is null) return;

        var bank = _session.LoadPoseBank(character);
        var clips = _session.LoadAnimations(character);

        Pose? pose = null;
        if (bank is not null && clips is not null && clips.Clips.Count > 2)
        {
            int poseIndex = clips.Clips[2].PoseIndices[0];
            if (poseIndex < bank.PoseCount) pose = bank.GetPose(poseIndex);
        }

        _viewport.Resize(700, 700);
        _viewport.Yaw = 0.7f;
        _viewport.Pitch = 0.1f;
        _viewport.Distance = 3.0f;
        _viewport.SetMesh(mesh, bank, pose);
        _viewport.Render(index =>
        {
            if (index < 0 || index >= character.Textures.TextureIds.Count) return null;
            int id = character.Textures.TextureIds[index];
            if (!_session.TryGetAsset(id, out var data) || !TextureFile.TryParse(data, out var tex)) return null;
            return _cache.Get(id, () => (tex.ToRgba(), tex.Width, tex.Height));
        });

        const int side = 700;
        var pixels = new byte[side * side * 4];
        _gl.BindTexture(TextureTarget.Texture2D, _viewport.ColourTexture);
        fixed (byte* p = pixels)
            _gl.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        // GL origin is bottom-left; flip to image order.
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < side; y++)
            Array.Copy(pixels, (side - 1 - y) * side * 4, flipped, y * side * 4, side * 4);

        File.WriteAllBytes(path, ImageCodec.RgbaToPng(flipped, side, side));
        Console.WriteLine($"wrote {path} (character mesh {character.MeshAssetId})");
    }

    /// <summary>
    /// The tab to switch to, set by a panel that sends the user somewhere else -- double-clicking a
    /// background in Rooms opens it in Backgrounds.
    /// </summary>
    private static string _requestedTab = "", _activeRequest = "";

    public static void ShowTab(string label) => _requestedTab = label;

    /// <summary>Splits a requested tab name on '/', so a sub-tab can be asked for too.</summary>
    private static string TopLevelOf(string request)
    {
        int slash = request.IndexOf('/');
        if (slash < 0) return request;

        string top = request[..slash], sub = request[(slash + 1)..];
        if (top == "Menus") MenusTab.Show(sub);
        else TextTab.Show(sub);
        return top;
    }

    private static bool BeginTab(string label)
    {
        bool wanted = label == _activeRequest
                      || (_windowShot is not null && _framesRendered < 3 && label == _forceTab);

        return TabBar.Begin(label, wanted);
    }

    /// <summary>
    /// Reads back the default framebuffer -- what the user actually sees -- and writes it to a PNG.
    /// </summary>
    private static unsafe void CaptureWindow(string path)
    {
        var size = _window.FramebufferSize;
        int width = size.X, height = size.Y;
        if (width <= 0 || height <= 0) return;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);

        var pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
            _gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        // GL origin is bottom-left; flip to image order.
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
            Array.Copy(pixels, (height - 1 - y) * width * 4, flipped, y * width * 4, width * 4);

        File.WriteAllBytes(path, ImageCodec.RgbaToPng(flipped, width, height));
        Console.WriteLine($"wrote {path} ({width}x{height} window)");

        File.WriteAllText(Path.ChangeExtension(path, ".labels.txt"),
            $"baseDirectory={AppContext.BaseDirectory}{Environment.NewLine}" +
            $"labelsPath={AssetLabels.Path}{Environment.NewLine}" +
            $"fallback={AssetLabels.UsingFallback}{Environment.NewLine}" +
            $"count={AssetLabels.Count}{Environment.NewLine}" +
            $"model:5746=\"{AssetLabels.Get(AssetLabels.Model, 5746)}\"{Environment.NewLine}");
        // Written beside the capture rather than to stdout: as a windowed application this has no
        // console of its own, and whether it can borrow the launching shell's varies.
        if (_pinClip >= 0 || _pinFrame >= 0)
            File.WriteAllText(Path.ChangeExtension(path, ".pose.txt"),
                _characterPanel.LastPoseInfo + Environment.NewLine);

        if (_startPlayback)
            File.WriteAllText(Path.ChangeExtension(path, ".playback.txt"),
                $"supported={AudioPlayer.Supported}{Environment.NewLine}" +
                $"playing={AudioPlayer.IsPlaying}{Environment.NewLine}" +
                $"index={AudioPlayer.PlayingIndex}{Environment.NewLine}" +
                $"position={AudioPlayer.Position:0.000}s{Environment.NewLine}" +
                SoundPanel.LastPlaybackResult + Environment.NewLine);
    }

    private static void DrawToolbar()
    {
        if (ImGui.Button("Open ROM...")) Browse();
        ImGui.SameLine();

        ImGui.SetNextItemWidth(520);
        if (ImGui.InputText("##rom", ref _romPath, 512, ImGuiInputTextFlags.EnterReturnsTrue)
            && File.Exists(_romPath))
            OpenRom(_romPath);

        ImGui.SameLine();
        ImGui.BeginDisabled(!File.Exists(_romPath));
        if (ImGui.Button("Load")) OpenRom(_romPath);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(_status);
        ImGui.Separator();
    }

    /// <summary>What the window shows before a ROM is loaded.</summary>
    private static void DrawWelcome()
    {
        var region = ImGui.GetContentRegionAvail();
        ImGui.Dummy(new Vector2(0, Math.Max(0, region.Y * 0.28f)));

        Centre("RE2 Studio");
        Centre("Resident Evil 2 (Nintendo 64) asset editor");
        ImGui.Dummy(new Vector2(0, 18));

        var size = new Vector2(240, 40);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - size.X) * 0.5f);
        if (ImGui.Button("Open a ROM...", size)) Browse();

        ImGui.Dummy(new Vector2(0, 14));
        Centre("or drop the path into the box at the top");

        if (_settings.LastRom is { Length: > 0 } last && !File.Exists(last))
        {
            ImGui.Dummy(new Vector2(0, 14));
            Centre($"(last used {Path.GetFileName(last)}, which is no longer there)");
        }

        ImGui.Dummy(new Vector2(0, 28));
        AboutPanel.DrawCompact();

        static void Centre(string text)
        {
            float width = ImGui.CalcTextSize(text).X;
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - width) * 0.5f);
            ImGui.TextDisabled(text);
        }
    }

    /// <summary>Shows the platform Open dialog, starting wherever the last ROM came from.</summary>
    private static void Browse()
    {
        string? start = null;
        if (_romPath.Length > 0) start = Path.GetDirectoryName(_romPath);
        else if (_settings.LastRom is { Length: > 0 } last) start = Path.GetDirectoryName(last);

        string? chosen = NativeDialogs.OpenFile("Open a Resident Evil 2 ROM", NativeDialogs.RomFilter, start);
        if (chosen is not null) OpenRom(chosen);
    }
}
