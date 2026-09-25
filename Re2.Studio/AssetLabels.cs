using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Re2.Studio;

/// <summary>
/// User-written names for assets -- "Ada Wong" on her models, "Raccoon City" on the street backgrounds
/// -- so a ROM full of numbered assets becomes searchable.
/// </summary>
public static class AssetLabels
{
    public const string FileName = "labels.json";

    public const string Background = "background";
    public const string Texture = "texture";
    public const string Model = "model";
    public const string Sound = "sound";
    public const string Voice = "voice";
    public const string Text = "text";
    public const string Movie = "movie";
    public const string Menu = "menu";
    public const string Icon = "icon";
    public const string Asset = "asset";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file actually in use, which may be the fallback.</summary>
    public static string Path { get; private set; } = "";

    /// <summary>Set when the preferred location beside the executable could not be written.</summary>
    public static bool UsingFallback { get; private set; }

    public static int Count => _labels.Count;

    /// <summary>The folder the executable actually sits in.</summary>
    private static string ExecutableFolder
    {
        get
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    string? folder = System.IO.Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(folder)) return folder;
                }
            }
            catch (Exception) { /* fall through to the runtime's own idea of the base directory */ }

            return AppContext.BaseDirectory;
        }
    }

    private static string PreferredPath =>
        System.IO.Path.Combine(ExecutableFolder, FileName);

    private static string FallbackPath =>
        System.IO.Path.Combine(Settings.Folder, FileName);

    public static void Load()
    {
        // Prefer a file that already exists, so an existing set of labels is never shadowed by an
        // empty one somewhere else.
        foreach (string candidate in new[] { PreferredPath, FallbackPath })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(candidate));
                if (loaded is not null)
                {
                    _labels = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                    Path = candidate;
                    UsingFallback = candidate == FallbackPath;
                    return;
                }
            }
            catch (Exception) { /* unreadable or malformed: try the next, then start empty */ }
        }

        _labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Path = PreferredPath;
        UsingFallback = false;
    }

    private static void Save()
    {
        // First run writes the file; if the install folder rejects it, move to the fallback once and
        // stay there for the rest of the session.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                string target = Path.Length > 0 ? Path : PreferredPath;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.WriteAllText(target, JsonSerializer.Serialize(Sorted(), Json));
                Path = target;
                return;
            }
            catch (Exception)
            {
                if (UsingFallback) return;          // already tried the fallback; give up quietly
                UsingFallback = true;
                Path = FallbackPath;
            }
        }
    }

    /// <summary>Sorted on the way out so the file diffs cleanly and reads in a sensible order.</summary>
    private static SortedDictionary<string, string> Sorted()
        => new(_labels, StringComparer.OrdinalIgnoreCase);

    /// <summary>The open ROM's build. Europe numbers its assets differently from the USA builds.</summary>
    public static Re2.Core.Rom.Re2Layout Layout { get; set; } = Re2.Core.Rom.Re2Layout.UsaRev1;

    /// <summary>
    /// Labels are keyed by Rev 1's numbering, so one file serves all four releases and a label made
    /// on one cart shows on the same asset in the others. Rev 0 numbers everything as Rev 1 does;
    /// Europe and Japan are translated (sounds and icons are numbered alike everywhere). What has
    /// no Rev 1 counterpart gets a key of its own region.
    /// </summary>
    private static string Key(string category, int id)
    {
        if (Layout.Rev1IdRuns is null || category is Sound or Icon) return $"{category}:{id}";

        int rev1 = category switch
        {
            Background => Layout.ToRev1Background(id),
            Voice => Layout.ToRev1VoiceClip(id),
            _ => Layout.ToRev1AssetId(id),
        };
        return rev1 >= 0 ? $"{category}:{rev1}" : RegionKey(category, id);
    }

    private static string RegionKey(string category, int id)
        => $"{(Layout == Re2.Core.Rom.Re2Layout.Japan ? "jp" : "eu")}-{category}:{id}";

    /// <summary>
    /// Where 1.1 kept a Europe or Japan background or voice label, before those were translated to
    /// Rev 1's numbering: still read, and moved to the shared key the next time it is edited.
    /// </summary>
    private static string? LegacyKey(string category, int id)
        => Layout.Rev1IdRuns is not null && category is Background or Voice ? RegionKey(category, id) : null;

    /// <summary>The label for an asset, or an empty string when it has none.</summary>
    public static string Get(string category, int id)
    {
        if (_labels.TryGetValue(Key(category, id), out var label)) return label;
        return LegacyKey(category, id) is { } legacy && _labels.TryGetValue(legacy, out label) ? label : "";
    }

    /// <summary>Sets or clears a label.</summary>
    public static void Set(string category, int id, string label)
    {
        string key = Key(category, id);
        string trimmed = label.Trim();

        bool changed = false;
        if (LegacyKey(category, id) is { } legacy && legacy != key) changed = _labels.Remove(legacy);

        if (trimmed.Length == 0) changed |= _labels.Remove(key);
        else
        {
            changed |= !_labels.TryGetValue(key, out var existing) || existing != trimmed;
            _labels[key] = trimmed;
        }

        if (changed) Save();
    }

    public static bool Has(string category, int id) => Get(category, id).Length > 0;

    /// <summary>Whether an item should survive the filter box.</summary>
    public static bool Matches(string category, int id, string filter, string? alsoSearch = null)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        string needle = filter.Trim();
        if (Get(category, id).Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (id.ToString().Contains(needle, StringComparison.Ordinal)) return true;

        return alsoSearch is not null && alsoSearch.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How many labels exist in one category, for the panels to report.</summary>
    public static int CountIn(string category)
        => _labels.Keys.Count(k => k.StartsWith(category + ":", StringComparison.OrdinalIgnoreCase));
}
