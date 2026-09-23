using System;
using ImGuiNET;
using Silk.NET.Input;

namespace Re2.Studio;

/// <summary>
/// A diagnostic for one question: when Ctrl or Shift is physically held, does the application see it,
/// and by which route?
/// </summary>
public static class ModifierProbe
{
    public static bool Enabled;

    /// <summary>Where the result is written, since a WinExe has no console.</summary>
    public static string ReportPath = "modifier-probe.txt";

    private static int _frames;
    private static bool _imguiCtrl, _imguiShift;
    private static bool _keyDownCtrl, _keyDownShift;
    private static bool _silkCtrl, _silkShift;
    private static bool _imguiDown, _silkDown, _wantText, _focused;

    /// <summary>Frames to sample. At 60fps this is roughly six seconds.</summary>
    private const int SampleFrames = 1200;

    public static void Sample(IInputContext input)
    {
        if (!Enabled) return;

        if (_frames == 0)
            try { System.IO.File.WriteAllText(ReportPath + ".pid",
                      Environment.ProcessId.ToString()); } catch (Exception) { }

        var io = ImGui.GetIO();

        // Route 1: the aggregate flags Selection currently reads.
        _imguiCtrl |= io.KeyCtrl;
        _imguiShift |= io.KeyShift;

        // Route 2: individual keys, which the backend feeds through AddKeyEvent.
        _keyDownCtrl |= ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);
        _keyDownShift |= ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);

        // Route 3: Silk's own keyboard, bypassing ImGui entirely.
        if (input.Keyboards.Count > 0)
        {
            var keyboard = input.Keyboards[0];
            _silkCtrl |= keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
            _silkShift |= keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
        }

        _imguiDown |= ImGui.IsKeyDown(ImGuiKey.DownArrow);
        _wantText |= io.WantTextInput;
        _focused |= io.AppFocusLost == false && ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow);
        if (input.Keyboards.Count > 0)
            _silkDown |= input.Keyboards[0].IsKeyPressed(Key.Down);

        if (++_frames < SampleFrames) return;

        var report = string.Join(Environment.NewLine, new[]
        {
            $"frames sampled       : {_frames}",
            $"io.KeyCtrl           : {_imguiCtrl}",
            $"io.KeyShift          : {_imguiShift}",
            $"IsKeyDown(LeftCtrl)  : {_keyDownCtrl}",
            $"IsKeyDown(LeftShift) : {_keyDownShift}",
            $"silk ControlLeft     : {_silkCtrl}",
            $"silk ShiftLeft       : {_silkShift}",
            $"IsKeyDown(DownArrow) : {_imguiDown}",
            $"silk Key.Down        : {_silkDown}",
            $"io.WantTextInput     : {_wantText}",
            $"any window focused   : {_focused}"
        });

        // A WinExe has no console attached, so the result has to go to a file to be readable.
        System.IO.File.WriteAllText(ReportPath, report);
        Environment.Exit(0);
    }
}
