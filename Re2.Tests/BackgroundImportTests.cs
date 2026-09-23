using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Project;
using Re2.Studio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Re2.Tests;

/// <summary>Backgrounds import through the project like everything else.</summary>
public sealed class BackgroundImportTests : IClassFixture<ProjectFixture>
{
    private readonly ProjectFixture _project;

    public BackgroundImportTests(ProjectFixture project) => _project = project;

    private static string WriteTestImage(string folder, int width, int height)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "replacement.png");

        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = ((x / 16) + (y / 16)) % 2 == 0
                        ? new Rgba32(220, 30, 30, 255)
                        : new Rgba32(30, 220, 30, 255);
            }
        });

        image.SaveAsPng(path);
        return path;
    }

    [RomFact]
    public void ImportWritesTheProjectFileAndTheRebuildPicksItUp()
    {
        using var session = new RomSession(TestRom.Path!);
        var target = session.Backgrounds[0];

        string scratch = Path.Combine(Path.GetTempPath(), "re2-bgimp-" + Path.GetRandomFileName()[..6]);
        var manifest = _project.Manifest!;

        int assetId = session.Assets.Entries.First(e => e.RomOffset == target.Offset).Index;
        var blob = manifest.Blobs.First(b => b.Ids.Contains(assetId));
        string projectFile = Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
        byte[] before = File.ReadAllBytes(projectFile);

        try
        {
            string png = WriteTestImage(scratch, target.Width, target.Height);

            string message = AssetIo.ImportBackground(session, _project.Folder, target, png);
            Assert.Contains("Build ROM", message);

            byte[] after = File.ReadAllBytes(projectFile);
            Assert.NotEqual(before, after);

            // Still a background the game could read, at the size it must be.
            Assert.True(BackgroundCodec.IsGameDecodable(after, out string why), why);
            var (_, width, height) = BackgroundCodec.JpegToRgba(after);
            Assert.Equal(target.Width, width);
            Assert.Equal(target.Height, height);

            // And a rebuild carries it into a ROM rather than leaving the original in place.
            var output = RomFileCopy(session);
            var result = ProjectFolder.Build(session.Rom, _project.Folder, output);
            Assert.Equal(1, result.BlobsRebuilt);
            Assert.False(result.ByteIdentical);

            var rebuiltIndex = BackgroundIndex.Build(output);
            var rebuilt = rebuiltIndex[0];
            Assert.Equal(target.Width, rebuilt.Width);
            Assert.Equal(target.Height, rebuilt.Height);
        }
        finally
        {
            File.WriteAllBytes(projectFile, before);          // leave the shared fixture as found
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The game reads a fixed frame size per room, so a differently sized replacement has to be
    /// refused rather than silently scaled into something that would render as garbage.
    /// </summary>
    [RomFact]
    public void ImportRefusesTheWrongSize()
    {
        using var session = new RomSession(TestRom.Path!);
        var target = session.Backgrounds[0];

        string scratch = Path.Combine(Path.GetTempPath(), "re2-bgsize-" + Path.GetRandomFileName()[..6]);
        try
        {
            string png = WriteTestImage(scratch, target.Width / 2, target.Height);
            var error = Assert.Throws<InvalidDataException>(
                () => AssetIo.ImportBackground(session, _project.Folder, target, png));
            Assert.Contains("must match exactly", error.Message);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static Re2.Core.Rom.RomFile RomFileCopy(RomSession session)
        => Re2.Core.Rom.RomFile.Load(session.Path);
}
