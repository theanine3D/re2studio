using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Re2.Core.Project;
using Re2.Core.Rom;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Re2.Tests;

/// <summary>Extracts the ROM once for the whole class.</summary>
public sealed class ProjectFixture : IDisposable
{
    public string Folder { get; }
    public ProjectManifest? Manifest { get; }

    public ProjectFixture()
    {
        Folder = Path.Combine(Path.GetTempPath(), "re2-project-" + Guid.NewGuid().ToString("N")[..8]);
        if (!TestRom.Available) return;
        Manifest = ProjectFolder.Extract(TestRom.Rom, Folder);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); }
        catch (IOException) { /* a locked temp file is not worth failing a test run over */ }
    }
}

public sealed class ProjectFolderTests : IClassFixture<ProjectFixture>
{
    private readonly ProjectFixture _project;

    public ProjectFolderTests(ProjectFixture project) => _project = project;

    private ProjectManifest Manifest => _project.Manifest!;

    /// <summary>
    /// Assets land in a folder named for what they are, under names matching what the editor calls them
    /// -- the Characters tab's "mesh 5746" is models/mesh5746.mesh.
    /// </summary>
    [RomFact]
    public void ExtractGroupsAssetsIntoNamedFolders()
    {
        var byCategory = Manifest.Blobs.GroupBy(b => b.Category)
                                       .ToDictionary(g => g.Key, g => g.Count());

        foreach (string category in new[] { "background", "model", "texture", "sound", "text", "animation", "menu" })
            Assert.True(byCategory.GetValueOrDefault(category) > 0, $"nothing classified as {category}");

        // Every background is found, and they keep the index the editor shows.
        Assert.Equal(1227, byCategory["background"]);
        Assert.Contains(Manifest.Blobs, b => b.File == "assets/backgrounds/bg0000.jpg");
        Assert.Contains(Manifest.Blobs, b => b.File == "assets/backgrounds/bg1226.jpg");

        // The five sound assets are named for their role rather than numbered.
        Assert.Contains(Manifest.Blobs, b => b.File == "assets/sounds/sample-data.bin");

        // Ada's two meshes, the example the naming exists to serve.
        Assert.Contains(Manifest.Blobs, b => b.File == "assets/models/mesh5746.mesh");

        foreach (var blob in Manifest.Blobs)
            Assert.True(File.Exists(Path.Combine(_project.Folder,
                            blob.File.Replace('/', Path.DirectorySeparatorChar))),
                        $"manifest lists {blob.File} but it was not written");
    }

    /// <summary>
    /// Two blobs must never be given the same path: the second would overwrite the first and the
    /// rebuild would silently use the wrong bytes for one of them.
    /// </summary>
    [RomFact]
    public void ExtractedNamesAreUnique()
    {
        var duplicates = Manifest.Blobs.GroupBy(b => b.File, StringComparer.OrdinalIgnoreCase)
                                       .Where(g => g.Count() > 1)
                                       .Select(g => g.Key)
                                       .ToList();

        Assert.True(duplicates.Count == 0, "duplicate paths: " + string.Join(", ", duplicates.Take(5)));
    }

    /// <summary>
    /// A blob reached through several ids has to be classified by whichever id identifies it, not
    /// just the lowest. Checking only the first filed most character meshes under "other".
    /// </summary>
    [RomFact]
    public void SharedBlobsAreClassifiedByAnyOfTheirIds()
    {
        int models = Manifest.Blobs.Count(b => b.Category == "model");
        Assert.True(models > 300, $"expected the great majority of the 355 meshes to be named, got {models}");
    }


    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private RomFile BuildRom(out ProjectFolder.BuildResult result)
    {
        var output = RomFile.Load(TestRom.Path!);
        result = ProjectFolder.Build(TestRom.Rom, _project.Folder, output);
        return output;
    }

    /// <summary>
    /// The property that makes the whole pipeline trustworthy: extract then rebuild with no edits
    /// must reproduce the cart image bit for bit, not merely something the game happens to accept.
    /// </summary>
    [RomFact]
    public void UneditedRebuildIsByteIdenticalToTheRetailRom()
    {
        var output = BuildRom(out var result);

        Assert.Equal(0, result.BlobsRebuilt);
        Assert.True(result.ByteIdentical, "asset region differs from the retail ROM");
        Assert.Equal(Hash(TestRom.Rom.Data), Hash(output.Data));
    }

    /// <summary>Every id the directory declares is either extracted or recorded as a null entry.</summary>
    [RomFact]
    public void EveryAssetIdIsAccountedForExactlyOnce()
    {
        var seen = new HashSet<int>();
        foreach (int id in Manifest.Blobs.SelectMany(b => b.Ids))
            Assert.True(seen.Add(id), $"asset id {id} was extracted more than once");

        foreach (int id in Manifest.NullIds)
            Assert.True(seen.Add(id), $"null id {id} also has a blob");

        Assert.Equal(Manifest.FileCount, seen.Count);
        Assert.Equal(Enumerable.Range(0, Manifest.FileCount).ToHashSet(), seen);
    }

    /// <summary>Several ids point at one blob (up to 55 on a single offset).</summary>
    [RomFact]
    public void SharedBlobsKeepAllTheirIds()
    {
        var shared = Manifest.Blobs.Where(b => b.Ids.Count > 1).ToList();
        Assert.NotEmpty(shared);

        var output = BuildRom(out _);
        var directory = AssetDirectory.Read(output);
        var byId = directory.Entries.ToDictionary(e => e.Index);

        foreach (var blob in shared)
        {
            var offsets = blob.Ids.Select(id => byId[id].Offset).Distinct().ToList();
            Assert.True(offsets.Count == 1, $"blob {blob.File} was split across {offsets.Count} offsets");
        }
    }

    /// <summary>Deflate blobs are extracted decoded, so the file on disk is the plain contents.</summary>
    [RomFact]
    public void ExtractedFilesMatchTheirRecordedHashAndSize()
    {
        foreach (var blob in Manifest.Blobs.Take(400))
        {
            string path = Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
            var data = File.ReadAllBytes(path);
            Assert.Equal(blob.Sha256, Hash(data));
            if (blob.Kind == AssetKind.Deflate) Assert.Equal(blob.DeclaredSize, data.Length);
        }
    }

    /// <summary>The reason the pipeline exists: an edited asset may be a different size.</summary>
    [RomFact]
    public void AGrownAssetRelocatesAndTheRomStillReads()
    {
        var source = TestRom.Rom;
        var sourceDirectory = AssetDirectory.Read(source);
        int backgroundsBefore = BackgroundIndex.Build(source).Count;

        // Pick the first background: a Stored blob whose contents we can legitimately re-encode.
        var entry = sourceDirectory.Entries.First(e => e.RomOffset == Re2RomMap.BackgroundsStart);
        var blob = Manifest.Blobs.First(b => b.Ids.Contains(entry.Index));
        string path = Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
        var original = File.ReadAllBytes(path);

        try
        {
            using var image = Image.Load<Rgba32>(original);
            var bigger = BackgroundCodec.EncodeBaseline(image, 100);
            Assert.True(bigger.Length > original.Length * 3 / 2,
                $"quality-100 re-encode was only {bigger.Length} bytes vs {original.Length}");
            File.WriteAllBytes(path, bigger);

            var output = BuildRom(out var result);

            Assert.Equal(1, result.BlobsRebuilt);
            Assert.False(result.ByteIdentical);
            Assert.True(result.BytesFree > 0, "the rebuilt region overran the cart");

            var rebuilt = AssetDirectory.Read(output);
            Assert.Equal(sourceDirectory.DeclaredFileCount, rebuilt.DeclaredFileCount);

            // The asset is bigger, and everything else still resolves.
            var grown = rebuilt.Entries.First(e => e.Index == entry.Index);
            Assert.Equal(bigger.Length, grown.StoredSize);
            Assert.True(grown.StoredSize > entry.StoredSize);

            // It is still a background, and no other background was lost in the relayout.
            Assert.True(Jfif.TryParse(output.Data, grown.RomOffset, out var image2, out _));
            Assert.Equal(image.Width, image2.Width);
            Assert.Equal(backgroundsBefore, BackgroundIndex.Build(output).Count);

            output.FixCrc();
            Assert.True(output.VerifyCrc());
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }
    }

    /// <summary>
    /// Expansion past the retail 64 MB is <b>not offered by the tools</b> -- an oversized cart is not
    /// known to run anywhere.
    /// </summary>
    [RomFact]
    public void AnOversizedProjectNeedsExpandAndThenBuildsCorrectly()
    {
        var blob = Manifest.Blobs.First(b => b.Kind == AssetKind.Stored && b.StoredSize > 4000);
        string path = Path.Combine(_project.Folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
        var original = File.ReadAllBytes(path);

        try
        {
            // Comfortably more than the ~193 KB of slack the retail layout leaves.
            var oversized = new byte[original.Length + 600 * 1024];
            original.CopyTo(oversized, 0);
            File.WriteAllBytes(path, oversized);

            // A retail-sized output can no longer hold it, and that must be a hard error rather
            // than a silent truncation. This is the guarantee that keeps builds bootable.
            var tooSmall = RomFile.Load(TestRom.Path!);
            var overflow = Assert.Throws<InvalidOperationException>(
                () => ProjectFolder.Build(TestRom.Rom, _project.Folder, tooSmall));
            Assert.Contains("Shrink an asset", overflow.Message);

            var expanded = RomFile.Load(TestRom.Path!).Expand(72 * 1024 * 1024);
            var result = ProjectFolder.Build(TestRom.Rom, _project.Folder, expanded);

            Assert.True(result.BytesUsed > RomFile.RetailLength,
                        $"the region ended at 0x{result.BytesUsed:X}, still inside 64 MB");
            Assert.True(result.BytesFree > 0);

            // The directory must still resolve, including the assets now living past the old limit.
            var directory = AssetDirectory.Read(expanded);
            Assert.Equal(Manifest.FileCount, directory.DeclaredFileCount);

            // Only the blob that outgrew its slot needs to move, so the expanded space holds it
            // alone rather than the hundreds of bystanders a repacking layout used to displace.
            var pastTheLine = directory.Entries.Where(e => e.RomOffset >= RomFile.RetailLength).ToList();
            Assert.True(pastTheLine.Count > 0, "nothing ended up past 64 MB, so the expansion was not exercised");
            foreach (var entry in pastTheLine)
                Assert.True(directory.TryGetData(expanded, entry, out _),
                            $"asset {entry.Index} at 0x{entry.RomOffset:X} would not decode");

            expanded.FixCrc();
            Assert.True(expanded.VerifyCrc(), "the CIC checksum did not survive expansion");
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }
    }

    [RomFact]
    public void ExpandRefusesToShrinkOrOverrunTheCartWindow()
    {
        var rom = TestRom.Rom;
        Assert.Throws<ArgumentException>(() => rom.Expand(rom.Length - 1));
        Assert.Throws<ArgumentException>(() => rom.Expand(RomFile.MaxAddressableLength + 1));

        var same = rom.Expand(rom.Length);
        Assert.Equal(rom.Length, same.Length);
        Assert.Equal(rom.GameCode, same.GameCode);
    }

    /// <summary>An expanded ROM is still recognised as this game; only the size changed.</summary>
    [RomFact]
    public void AnExpandedRomIsStillRecognised()
    {
        var expanded = TestRom.Rom.Expand(80 * 1024 * 1024);

        Assert.True(Re2RomMap.IsExpectedRom(expanded));
        Assert.True(Re2RomMap.IsExpanded(expanded));
        Assert.False(Re2RomMap.IsExpanded(TestRom.Rom));
    }
}
