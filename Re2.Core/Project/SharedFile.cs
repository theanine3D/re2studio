using System.IO;
using System.Text;

namespace Re2.Core.Project;

/// <summary>Reads that do not lock the file against anyone else.</summary>
public static class SharedFile
{
    private const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>Opens a file for reading without excluding anyone else from writing to it.</summary>
    public static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, Share);

    public static byte[] ReadAllBytes(string path)
    {
        using var stream = OpenRead(path);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static string ReadAllText(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
