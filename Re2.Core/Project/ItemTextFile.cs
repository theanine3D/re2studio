using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Re2.Core.Assets;

namespace Re2.Core.Project;

/// <summary>The item examine text as a file in a project folder.</summary>
public static class ItemTextFile
{
    public const string Name = "text/item-text.txt";

    public static string PathIn(string folder)
        => Path.Combine(folder, Name.Replace('/', Path.DirectorySeparatorChar));

    public static bool ExistsIn(string folder) => File.Exists(PathIn(folder));

    private const string Preamble =
        "# The item examine text, one record per '#number' marker, in the order the game indexes\n" +
        "# them. A blank line is part of the record it sits in.\n" +
        "#\n" +
        "# <FD> starts a new page, <FE> ends a message, and <XX> is any other code whose meaning is\n" +
        "# not known -- all three type back exactly as they read. The editor's Text tab shows the\n" +
        "# same thing with a byte budget beside it.\n";

    public static void Write(string folder, IReadOnlyList<string> records)
    {
        string path = PathIn(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var text = new StringBuilder(Preamble.Replace("\n", Environment.NewLine));

        for (int i = 0; i < records.Count; i++)
        {
            text.Append('#').Append(i).Append(Environment.NewLine);
            text.Append(records[i].Replace("\n", Environment.NewLine)).Append(Environment.NewLine);
        }

        ProjectFolder.WritePatiently(path, Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>Reads the records back.</summary>
    public static bool TryRead(string folder, out List<string> records, out string error)
    {
        var found = new List<string>();
        records = found;
        error = "";

        string path = PathIn(folder);
        if (!File.Exists(path)) { error = $"{Name} is not in the project folder."; return false; }

        string text = SharedFile.ReadAllText(path).Replace("\r\n", "\n");

        // The file ends with a newline; that one terminates the file rather than the last record.
        if (text.EndsWith("\n", StringComparison.Ordinal)) text = text[..^1];

        var lines = text.Split('\n');

        var current = new List<string>();
        bool started = false;

        void Close()
        {
            if (!started) return;

            found.Add(string.Join("\n", current));
            current.Clear();
        }

        foreach (string line in lines)
        {
            if (IsMarker(line, found.Count + (started ? 1 : 0), out string markerError))
            {
                if (markerError.Length > 0) { error = markerError; return false; }
                Close();
                started = true;
                continue;
            }

            if (started) current.Add(line);
        }

        Close();

        if (found.Count != ItemMessages.Count)
        {
            error = $"{Name} holds {found.Count} records but there must be exactly " +
                    $"{ItemMessages.Count}. A record's number is what the game looks it up by, so " +
                    "markers cannot be added or removed.";
            return false;
        }

        return true;
    }

    /// <summary>Is this a record marker, and if so is it the one expected next?</summary>
    private static bool IsMarker(string line, int expected, out string error)
    {
        error = "";

        string trimmed = line.TrimEnd();
        if (trimmed.Length < 2 || trimmed[0] != '#') return false;
        if (!int.TryParse(trimmed.AsSpan(1), out int number)) return false;

        if (number != expected)
            error = $"{Name}: found marker #{number} where #{expected} was expected. " +
                    "The records must stay in order and none may be left out.";

        return true;
    }
}
