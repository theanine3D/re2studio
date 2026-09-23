using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Re2.Studio;

/// <summary>
/// The handful of things worth remembering between runs, so the tool opens where it was left rather
/// than asking for the same ROM every time.
/// </summary>
public sealed class Settings
{
    [JsonPropertyName("lastRom")] public string? LastRom { get; set; }
    [JsonPropertyName("lastProject")] public string? LastProject { get; set; }
    [JsonPropertyName("lastExportFolder")] public string? LastExportFolder { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder
        => Environment.GetEnvironmentVariable("RE2_SETTINGS_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "RE2Suite");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        // Corrupt or unreadable: start fresh rather than fail.
        return Read() ?? new Settings();
    }

    /// <summary>
    /// Writes these settings over whatever is on disk, keeping anything this instance does not know
    /// about.
    /// </summary>
    public void Save()
    {
        try
        {
            var merged = Read() ?? new Settings();

            merged.LastRom = LastRom ?? merged.LastRom;
            merged.LastProject = LastProject ?? merged.LastProject;
            merged.LastExportFolder = LastExportFolder ?? merged.LastExportFolder;

            // Adopt the result, so this instance stops carrying gaps another one has since filled.
            LastRom = merged.LastRom;
            LastProject = merged.LastProject;
            LastExportFolder = merged.LastExportFolder;

            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(merged, Json));
        }
        catch (Exception) { /* nothing here is worth interrupting the user over */ }
    }

    private static Settings? Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath))
                : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Records a project folder so the next run finds the extract that is already there.
    /// </summary>
    public void RememberProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || LastProject == path) return;
        LastProject = path;
        Save();
    }

    /// <summary>Records a ROM as the one to reopen next time.</summary>
    public void RememberRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        LastRom = path;
        Save();
    }
}
