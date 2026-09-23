using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Re2.Core.Assets;
using Re2.Core.Project;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Importing a movie into a project that predates the movie classifier.</summary>
public sealed class FmvImportTests
{
    private readonly ITestOutputHelper _out;
    public FmvImportTests(ITestOutputHelper o) => _out = o;

    [RomFact]
    public void ImportingIntoAStaleProjectRehomesTheBlob()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);
        var movie = FmvIndex.Build(rom, directory).First();

        string project = Path.Combine(Path.GetTempPath(), "re2-rehome-" + Guid.NewGuid().ToString("N"));

        try
        {
            // A project as an older build would have written one: the movie filed under "other".
            var entry = directory.Entries.First(e => e.Index == movie.AssetId);
            Assert.True(directory.TryGetData(rom, entry, out var original));

            string stalePath = $"assets/other/{movie.AssetId:D5}_Stored.bin";
            Directory.CreateDirectory(Path.Combine(project, "assets", "other"));
            File.WriteAllBytes(Path.Combine(project, stalePath.Replace('/', Path.DirectorySeparatorChar)),
                               original);

            var manifest = new ProjectManifest
            {
                Rom = "test",
                AssetBase = directory.AssetBaseRomOffset,
                FileCount = directory.DeclaredFileCount,
                Blobs =
                {
                    new ProjectBlob
                    {
                        Ids = { movie.AssetId },
                        Category = "other",
                        File = stalePath,
                        Kind = entry.Kind,
                        StoredSize = movie.StoredSize,
                        DeclaredSize = movie.StoredSize,
                        OriginalOffset = entry.RomOffset - directory.AssetBaseRomOffset,
                        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)),
                    },
                },
            };

            File.WriteAllText(Path.Combine(project, ProjectFolder.ManifestName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            // A replacement with no name of its own, which is what a converted file looks like.
            int headerEnd = Re2.Core.Formats.Mpeg1Sequence.HeaderLength(original);
            int bodyAt = Re2.Core.Formats.Mpeg1Sequence.Find(original,
                                                             Re2.Core.Formats.Mpeg1Sequence.GroupStart);

            var nameless = original[..headerEnd].Concat(original[bodyAt..]).ToArray();
            Assert.Equal("", FmvIndex.ReadName(nameless));

            string source = Path.Combine(project, "replacement.m2v");
            File.WriteAllBytes(source, nameless);

            var session = new Re2.Studio.RomSession(TestRom.Path!);
            string log = Re2.Studio.AssetIo.ImportMovie(session, project, movie, source);

            _out.WriteLine(log);

            var after = JsonSerializer.Deserialize<ProjectManifest>(
                File.ReadAllText(Path.Combine(project, ProjectFolder.ManifestName)))!;
            var blob = after.Blobs.Single();

            _out.WriteLine($"manifest now: category \"{blob.Category}\" at {blob.File}");

            Assert.Equal("movie", blob.Category);
            Assert.StartsWith("assets/movies/", blob.File);
            Assert.EndsWith(".m2v", blob.File);

            // The file really moved, and the old one is gone rather than left as a duplicate.
            Assert.True(File.Exists(Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar))));
            Assert.False(File.Exists(Path.Combine(project, stalePath.Replace('/', Path.DirectorySeparatorChar))));

            // The slot's name is carried into the replacement, so the list still identifies it.
            var written = File.ReadAllBytes(
                Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar)));

            _out.WriteLine($"name in the imported stream: \"{FmvIndex.ReadName(written)}\"");
            Assert.Equal(movie.Name, FmvIndex.ReadName(written));

            // And the recorded hash still describes what the ROM shipped, so "edited" still works.
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)),
                         blob.Sha256);
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch (IOException) { }
        }
    }
}
