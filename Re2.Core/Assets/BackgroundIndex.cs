using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>One prerendered background slot in the ROM.</summary>
public sealed record Background(int Index, JfifImage Image)
{
    public int Offset => Image.Offset;
    public int Length => Image.Length;
    public int Width => Image.Width;
    public int Height => Image.Height;

    /// <summary>
    /// Bytes available to a replacement image before it would collide with the next background.
    /// </summary>
    public int SlotCapacity { get; init; }

    public override string ToString() => $"bg{Index:D4} {Image}";
}

/// <summary>Locates the prerendered backgrounds.</summary>
public sealed class BackgroundIndex
{
    public IReadOnlyList<Background> Backgrounds { get; }

    private BackgroundIndex(IReadOnlyList<Background> backgrounds) => Backgrounds = backgrounds;

    public int Count => Backgrounds.Count;
    public Background this[int index] => Backgrounds[index];

    /// <summary>
    /// Indexes the backgrounds via the asset directory, which is the only approach that survives a
    /// rebuild: a scan over a fixed ROM range silently loses images once the region has been relaid
    /// out and everything has shifted. On the retail cart both routes find the same 1,227 images.
    /// </summary>
    public static BackgroundIndex Build(RomFile rom)
    {
        try { return BuildFromDirectory(rom, AssetDirectory.Read(rom)); }
        catch (Exception)
        {
            // No readable directory (a foreign or damaged ROM): fall back to scanning the region.
            return Build(rom, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);
        }
    }

    public static BackgroundIndex BuildFromDirectory(RomFile rom, AssetDirectory directory)
    {
        // 196 blobs are pointed at by more than one asset id, so the entry list must be reduced to
        // distinct offsets first -- otherwise a shared background is listed once per id.
        var found = new List<JfifImage>();
        var seen = new HashSet<int>();

        foreach (var entry in directory.Entries)
        {
            if (entry.Kind != AssetKind.Stored) continue;
            if (entry.StoredSize < 8) continue;
            if (!seen.Add(entry.RomOffset)) continue;
            if (!Jfif.TryParse(rom.Data, entry.RomOffset, out var image, out _)) continue;
            if (image.Length > entry.StoredSize) continue;
            found.Add(image);
        }

        found.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var list = new List<Background>(found.Count);
        for (int i = 0; i < found.Count; i++)
        {
            int next = i + 1 < found.Count ? found[i + 1].Offset : found[i].End + Re2RomMap.InterFileGap;
            int capacity = next - found[i].Offset - Re2RomMap.InterFileGap;
            list.Add(new Background(i, found[i]) { SlotCapacity = Math.Max(found[i].Length, capacity) });
        }

        return new BackgroundIndex(list);
    }

    public static BackgroundIndex Build(RomFile rom, int start, int end)
    {
        var images = Jfif.ScanRange(rom.Data, start, end);
        var list = new List<Background>(images.Count);

        for (int i = 0; i < images.Count; i++)
        {
            // A replacement may grow into the gap before the next image, but no further.
            int next = i + 1 < images.Count ? images[i + 1].Offset : end;
            int capacity = next - images[i].Offset - Re2RomMap.InterFileGap;
            list.Add(new Background(i, images[i]) { SlotCapacity = Math.Max(0, capacity) });
        }

        return new BackgroundIndex(list);
    }

    /// <summary>Raw JFIF bytes for a background, exactly as stored.</summary>
    public ReadOnlySpan<byte> GetJpegBytes(RomFile rom, int index)
    {
        var bg = Backgrounds[index];
        return rom.Slice(bg.Offset, bg.Length);
    }

    /// <summary>
    /// How many consecutive backgrounds are packed with the container's expected gap-then-align rule.
    /// </summary>
    public int CountAdjacentPairsMatchingPacking()
    {
        int matches = 0;
        for (int i = 0; i + 1 < Backgrounds.Count; i++)
        {
            int predicted = Re2RomMap.NextFileOffset(Backgrounds[i].Offset, Backgrounds[i].Length);
            if (predicted == Backgrounds[i + 1].Offset) matches++;
        }
        return matches;
    }

    public IEnumerable<(string Dimensions, int Count)> DimensionHistogram()
        => Backgrounds
            .GroupBy(b => $"{b.Width}x{b.Height}")
            .OrderByDescending(g => g.Count())
            .Select(g => (g.Key, g.Count()));
}
