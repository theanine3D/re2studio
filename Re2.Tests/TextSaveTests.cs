using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Project;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>
/// Saving a string from the Text tab into the project folder -- the path the Save button takes.
/// </summary>
public sealed class TextSaveTests : IClassFixture<ProjectFixture>
{
    private readonly ProjectFixture _project;

    public TextSaveTests(ProjectFixture project) => _project = project;

    private string FileFor(int assetId)
    {
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(
            File.ReadAllText(Path.Combine(_project.Folder, ProjectFolder.ManifestName)))!;

        var blob = manifest.Blobs.First(b => b.Ids.Contains(assetId));
        return Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The saved file must be exactly what the rebuild reads for that asset -- the same encoding the
    /// command-line import uses -- and must land in that asset's own .txt, not a new file beside it.
    /// </summary>
    [RomFact]
    public void SavingWritesTheAssetsOwnTextFileInTheGamesEncoding()
    {
        var rom = TestRom.Rom;
        var entry = TextTable.Read(rom, AssetDirectory.Read(rom)).First();

        string edited = entry.Text.Replace("\r\n", "\n") + "\nAn added line.";
        string report = AssetIo.SaveText(_project.Folder, entry.AssetId, edited, entry.Text.Length);

        string file = FileFor(entry.AssetId);
        var bytes = File.ReadAllBytes(file);

        Assert.Equal(TextTable.Encode(edited), bytes);
        Assert.EndsWith(".txt", file);
        Assert.Contains(entry.AssetId.ToString(), report);

        // CRLF on disk, whatever the editor held -- that is the game's own line break.
        Assert.Contains("\r\nAn added line.", System.Text.Encoding.ASCII.GetString(bytes));
    }

    /// <summary>
    /// Text the game's font cannot draw is refused outright, and the file is left as it was rather
    /// than half-written.
    /// </summary>
    [RomFact]
    public void TextTheFontCannotDrawIsRefusedAndNothingIsWritten()
    {
        var rom = TestRom.Rom;
        var entry = TextTable.Read(rom, AssetDirectory.Read(rom)).Skip(1).First();

        string file = FileFor(entry.AssetId);
        var before = File.ReadAllBytes(file);

        Assert.ThrowsAny<ArgumentException>(
            () => AssetIo.SaveText(_project.Folder, entry.AssetId, "Café", entry.Text.Length));

        Assert.Equal(before, File.ReadAllBytes(file));
    }
}
