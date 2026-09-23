using System;
using System.Collections.Generic;
using System.IO;
using Re2.Core.Assets;

namespace Re2.Core.Project;

/// <summary>The inventory item names as a file in a project folder.</summary>
public static class ItemNameFile
{
    public const string Name = "text/item-names.txt";

    public static string PathIn(string folder)
        => Path.Combine(folder, Name.Replace('/', Path.DirectorySeparatorChar));

    public static bool ExistsIn(string folder) => File.Exists(PathIn(folder));

    /// <summary>Writes the ROM's own names, which is what an extract starts from.</summary>
    public static void Write(string folder, IReadOnlyList<string> names)
    {
        string path = PathIn(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A trailing newline after the last name, and no other trailing blank line: a stray blank
        // would read back as a 157th name and be rejected.
        ProjectFolder.WritePatiently(
            path,
            System.Text.Encoding.UTF8.GetBytes(
                string.Join(Environment.NewLine, names) + Environment.NewLine));
    }

    /// <summary>Reads the names back.</summary>
    public static bool TryRead(string folder, out List<string> names, out string error)
    {
        names = new List<string>();
        error = "";

        string path = PathIn(folder);
        if (!File.Exists(path)) { error = $"{Name} is not in the project folder."; return false; }

        string text = SharedFile.ReadAllText(path);
        var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));

        // The newline the file ends with is a terminator, not an empty last name.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        if (lines.Count != ItemNames.Count)
        {
            error = $"{Name} has {lines.Count} lines but there must be exactly {ItemNames.Count}, " +
                    "one per item id. Add or remove lines rather than deleting names -- a name's " +
                    "line number is what the game looks it up by.";
            return false;
        }

        names = lines;
        return true;
    }
}
