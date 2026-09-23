using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Formats;
using Xunit;

namespace Re2.Tests;

public class ModelFileTests
{
    private static List<(AssetEntry Entry, ModelFile Model)> LoadModels()
    {
        var rom = TestRom.Rom;
        var dir = AssetDirectory.Read(rom);
        var models = new List<(AssetEntry, ModelFile)>();

        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (ModelFile.TryParse(data, out var model)) models.Add((entry, model));
        }
        return models;
    }

    [RomFact]
    public void FindsTheModelSet()
    {
        var models = LoadModels();
        Assert.Equal(143, models.Count);
        Assert.Equal(1984, models[0].Entry.Index);
        Assert.Equal(2126, models[^1].Entry.Index);
    }

    /// <summary>Model ids form one contiguous run, which is what makes them identifiable as a set.</summary>
    [RomFact]
    public void ModelIdsAreContiguous()
    {
        var ids = LoadModels().Select(m => m.Entry.Index).ToList();
        for (int i = 1; i < ids.Count; i++)
            Assert.Equal(ids[i - 1] + 1, ids[i]);
    }

    [RomFact]
    public void SectionsTileTheFileWithoutGaps()
    {
        foreach (var (entry, model) in LoadModels())
        {
            var present = model.Sections.Where(s => s.Size > 0).OrderBy(s => s.Offset).ToList();
            Assert.Equal(ModelFile.HeaderSize, present[0].Offset);
            Assert.Equal(model.Data.Length, present[^1].End);

            for (int i = 1; i < present.Count; i++)
                Assert.True(present[i - 1].End == present[i].Offset,
                    $"asset {entry.Index}: section {i - 1} ends at 0x{present[i - 1].End:X} but section {i} starts at 0x{present[i].Offset:X}");
        }
    }

    [RomFact]
    public void EverySectionStaysInsideTheFile()
    {
        foreach (var (entry, model) in LoadModels())
            foreach (var section in model.Sections)
                Assert.True(section.End <= model.Data.Length,
                    $"asset {entry.Index} section {section.Index} runs past the end of the file");
    }

    [RomFact]
    public void GeometryBlockOffsetsAreOrderedAndInsideSectionOne()
    {
        foreach (var (entry, model) in LoadModels())
        {
            var section = model.Sections[1];
            var offsets = model.GeometryBlockOffsets;
            Assert.NotEmpty(offsets);

            // The table ends exactly where its first entry points.
            Assert.Equal(section.Offset + offsets.Count * 4, offsets[0]);

            foreach (var offset in offsets)
                Assert.InRange(offset, section.Offset, section.End);
        }
    }

    /// <summary>
    /// Section 1 carries one or two geometry blocks per part -- the split Capcom's MD1 uses for a
    /// part's triangles and quads. Anything outside that range would mean the layout is wrong.
    /// </summary>
    [RomFact]
    public void GeometryBlockCountTracksPartCount()
    {
        foreach (var (entry, model) in LoadModels())
        {
            if (model.PartCount == 0) continue;
            Assert.InRange(model.GeometryBlockOffsets.Count, model.PartCount, model.PartCount * 2 + 1);
        }
    }

    [RomFact]
    public void PartCountsAreSane()
    {
        var models = LoadModels();
        Assert.All(models, m => Assert.InRange(m.Model.PartCount, 0, 64));
        Assert.Contains(models, m => m.Model.PartCount >= 10);
    }

    [RomFact]
    public void ClipTablesParseWhenPresent()
    {
        foreach (var (entry, model) in LoadModels())
        {
            if (model.Sections[3].Size == 0) { Assert.Empty(model.Clips); continue; }
            Assert.NotEmpty(model.Clips);
            Assert.All(model.Clips, c => Assert.True(c.Count >= 0));
        }
    }

    /// <summary>Field4 mirrors the first clip's count wherever a clip table exists.</summary>
    [RomFact]
    public void HeaderFieldMatchesFirstClipCount()
    {
        int checked_ = 0;
        foreach (var (entry, model) in LoadModels())
        {
            if (model.Clips.Count == 0) continue;
            Assert.Equal((uint)model.Clips[0].Count, model.Field4);
            checked_++;
        }
        Assert.True(checked_ > 0, "no model carried a clip table");
    }

    [Fact]
    public void RejectsNonModelData()
    {
        Assert.False(ModelFile.LooksLikeModel(new byte[8]));
        Assert.False(ModelFile.LooksLikeModel(new byte[256]));
        Assert.False(ModelFile.TryParse(new byte[256], out _));
    }
}
