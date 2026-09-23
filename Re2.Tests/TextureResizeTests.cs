using System.IO;
using System.Linq;
using Re2.Core.Formats;
using Re2.Studio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Re2.Tests;

/// <summary>Replacement textures of the wrong size are resized rather than refused.</summary>
public sealed class TextureResizeTests : IClassFixture<ProjectFixture>
{
    private readonly ProjectFixture _project;

    public TextureResizeTests(ProjectFixture project) => _project = project;

    private string FileFor(int assetId)
    {
        var blob = _project.Manifest!.Blobs.First(b => b.Ids.Contains(assetId));
        return Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>A recognisable image: a bright cross on dark, so a resize is still identifiable.</summary>
    private static string WritePng(string folder, int width, int height)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{width}x{height}.png");

        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = (x > width / 3 && x < 2 * width / 3) || (y > height / 3 && y < 2 * height / 3)
                        ? new Rgba32(240, 40, 40, 255)
                        : new Rgba32(20, 20, 40, 255);
            }
        });

        image.SaveAsPng(path);
        return path;
    }

    private void WithTexture(System.Action<RomSession, int, TextureFile, string> body)
    {
        using var session = new RomSession(TestRom.Path!);
        var (entry, texture) = session.Textures[0];

        string projectFile = FileFor(entry.Index);
        byte[] original = File.ReadAllBytes(projectFile);
        string scratch = Path.Combine(Path.GetTempPath(), "re2-resize-" + Path.GetRandomFileName()[..6]);

        try
        {
            body(session, entry.Index, texture, scratch);
        }
        finally
        {
            File.WriteAllBytes(projectFile, original);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    [RomFact]
    public void AnOversizedImageIsResizedAndImported()
    {
        WithTexture((session, assetId, texture, scratch) =>
        {
            // Slightly too big, which is the case that used to be refused outright.
            string png = WritePng(scratch, texture.Width + 7, texture.Height + 3);

            string report = AssetIo.ImportTexture(session, _project.Folder, assetId, png);

            Assert.Contains("resized from", report);
            Assert.Contains($"{texture.Width}x{texture.Height}", report);

            // What landed is a texture of the ROM's own size, not the file's.
            var written = File.ReadAllBytes(FileFor(assetId));
            Assert.True(TextureFile.TryParse(written, out var stored));
            Assert.Equal(texture.Width, stored.Width);
            Assert.Equal(texture.Height, stored.Height);
        });
    }

    [RomFact]
    public void AnUndersizedImageIsResizedTheSameWay()
    {
        WithTexture((session, assetId, texture, scratch) =>
        {
            if (texture.Width < 4 || texture.Height < 4) return;

            string png = WritePng(scratch, texture.Width / 2, texture.Height / 2);
            string report = AssetIo.ImportTexture(session, _project.Folder, assetId, png);

            Assert.Contains("resized from", report);

            var written = File.ReadAllBytes(FileFor(assetId));
            Assert.True(TextureFile.TryParse(written, out var stored));
            Assert.Equal(texture.Width, stored.Width);
            Assert.Equal(texture.Height, stored.Height);
        });
    }

    /// <summary>
    /// A different shape is stretched to fit, and said so -- that usually means the wrong file was
    /// picked, and it is the one case where the result will look wrong rather than merely softer.
    /// </summary>
    [RomFact]
    public void ADifferentAspectRatioIsReportedAsStretched()
    {
        WithTexture((session, assetId, texture, scratch) =>
        {
            string png = WritePng(scratch, texture.Width * 3, texture.Height);
            string report = AssetIo.ImportTexture(session, _project.Folder, assetId, png);

            Assert.Contains("stretched", report);
        });
    }

    /// <summary>
    /// The common case must not be reported as a resize, or the message becomes noise nobody reads.
    /// </summary>
    [RomFact]
    public void ACorrectlySizedImageIsNotReportedAsResized()
    {
        WithTexture((session, assetId, texture, scratch) =>
        {
            string png = WritePng(scratch, texture.Width, texture.Height);
            string report = AssetIo.ImportTexture(session, _project.Folder, assetId, png);

            Assert.DoesNotContain("resized", report);
            Assert.DoesNotContain("stretched", report);
        });
    }
}
