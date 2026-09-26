using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Re2.Core.Assets;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Core.Import;
using Re2.Core.Codecs;
using Re2.Core.Project;
using Re2.Core.Rom;
using Re2.Core.Video;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Re2.Studio;

/// <summary>Export and import for backgrounds, meshes and textures, driven from the window.</summary>
public static class AssetIo
{
    // ---- textures ---------------------------------------------------------

    /// <summary>Writes one texture out as a PNG. Returns the path written.</summary>
    public static string ExportTexturePng(RomSession session, int assetId, string folder)
    {
        if (!session.TryGetAsset(assetId, out var data) || !TextureFile.TryParse(data, out var texture))
            throw new InvalidDataException($"Asset {assetId} is not a texture.");

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"texture{assetId}_{texture.Width}x{texture.Height}.png");
        File.WriteAllBytes(path, ImageCodec.RgbaToPng(texture.ToRgba(), texture.Width, texture.Height));
        return path;
    }

    public static string ExportTextures(RomSession session, IEnumerable<int> assetIds, string folder)
    {
        int written = 0, failed = 0;
        string? last = null;

        foreach (int id in assetIds)
        {
            try { last = ExportTexturePng(session, id, folder); written++; }
            catch (Exception) { failed++; }
        }

        return written == 1 && failed == 0
            ? $"wrote {last}"
            : $"wrote {written:N0} PNGs to {folder}" + (failed > 0 ? $" ({failed:N0} would not decode)" : "");
    }

    /// <summary>Replaces a texture in a project folder.</summary>
    public static string ImportTexture(RomSession session, string project, int assetId, string pngPath)
    {
        using var image = Image.Load<Rgba32>(pngPath);
        var (file, report) = ReplaceTexture(session, project, assetId, image, Path.GetFileName(pngPath));

        return file is null
            ? $"texture {assetId}: {report}"
            : $"texture {assetId}: {report}\nwrote {file} -- now Build ROM on the Project tab";
    }

    /// <summary>Writes one already-decoded image over a texture in a project.</summary>
    private static (string? File, string Report) ReplaceTexture(RomSession session, string project,
                                                               int assetId, Image<Rgba32> image,
                                                               string sourceName)
    {
        if (!session.TryGetAsset(assetId, out var data) || !TextureFile.TryParse(data, out var existing))
            throw new InvalidDataException($"Asset {assetId} is not a texture.");

        string resizeNote = "";

        if (image.Width != existing.Width || image.Height != existing.Height)
        {
            // Worth saying when the shape changed too, not just the size: a different aspect ratio
            // means the image is stretched to fit, which usually means the wrong file was picked.
            bool stretched = image.Width * existing.Height != existing.Width * image.Height;

            resizeNote = $"resized from {image.Width}x{image.Height} to " +
                         $"{existing.Width}x{existing.Height}" +
                         (stretched ? " (stretched -- the source had a different shape)" : "") + "; ";

            // Lanczos keeps detail through a downscale, which is what these almost always are.
            image.Mutate(x => x.Resize(existing.Width, existing.Height, KnownResamplers.Lanczos3));
        }

        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);

        var (bytes, report) = TextureWriter.Replace(existing, rgba, image.Width, image.Height);

        if (bytes.AsSpan().SequenceEqual(data)) return (null, resizeNote + "unchanged");

        return (WriteProjectAsset(project, assetId, bytes), resizeNote + report);
    }

    // ---- backgrounds ------------------------------------------------------

    /// <summary>Replaces a background in a project folder.</summary>
    public static string ImportBackground(RomSession session, string project, Background target, string pngPath)
    {
        int assetId = BackgroundAssetId(session, target);

        // What the background holds now -- the project's edit if there is one -- decides whether this
        // import changes anything at all.
        var current = session.GetBackgroundJpeg(target.Index);
        var encoded = BackgroundCodec.EncodeForProject(pngPath, target, current, target.Length);

        if (!encoded.Unchanged && !BackgroundCodec.IsGameDecodable(encoded.Data, out string why))
            throw new InvalidDataException("Encoder produced something the game cannot decode: " + why);

        string file = WriteProjectAsset(project, assetId, encoded.Data);

        string how = encoded.Unchanged
            ? $"unchanged image, original {encoded.Size:N0} bytes kept"
            : encoded.FitsSlot
                ? $"quality {encoded.Quality}, {encoded.Size:N0} bytes of the original {target.Length:N0}"
                : $"{encoded.Size:N0} bytes even at quality {encoded.Quality} -- " +
                  $"{encoded.Size - target.Length:N0} more than the original {target.Length:N0}, " +
                  "which the rebuild allows but takes from the shared region";

        return $"bg{target.Index:D4} (asset {assetId}): {how}\n" +
               $"wrote {file} -- now Build ROM on the Project tab";
    }

    /// <summary>The asset id holding a background.</summary>
    private static int BackgroundAssetId(RomSession session, Background target)
    {
        var entry = session.Assets.Entries.FirstOrDefault(e => e.RomOffset == target.Offset)
                    ?? throw new InvalidDataException(
                        $"bg{target.Index:D4} is not in the asset directory, so it cannot be imported.");
        return entry.Index;
    }

    // ---- text -------------------------------------------------------------

    /// <summary>Writes one edited string into its .txt in the project folder.</summary>
    public static string SaveText(string project, int assetId, string text, int previousLength,
                                  bool latin1 = false)
    {
        if (!HasProject(project))
            throw new FileNotFoundException("No extracted project. Use Extract on the Project tab first.");

        // Refuses anything the game's font has no glyph for, rather than writing mojibake.
        var bytes = TextTable.Encode(text, latin1);

        string file = WriteProjectAsset(project, assetId, bytes);

        string size = bytes.Length == previousLength
            ? $"{bytes.Length:N0} bytes"
            : $"{bytes.Length:N0} bytes, was {previousLength:N0}";

        return $"saved text {assetId} to {file} ({size})";
    }

    // ---- menu screens -----------------------------------------------------

    /// <summary>One menu screen as a PNG, drawn with the palette asked for.</summary>
    public static string ExportMenuImagePng(RomSession session, int assetId, string folder, int palette = -1)
    {
        if (!session.TryGetAsset(assetId, out var data) || !MenuImage.TryParse(data, out var image))
            throw new InvalidDataException($"Asset {assetId} is not a menu screen.");

        Directory.CreateDirectory(folder);

        int shown = palette < 0 ? image!.DisplayPalette : palette;
        string path = Path.Combine(folder, $"menu{assetId}_{image!.Width}x{image.Height}" +
                                           (image.Palettes.Count > 1 ? $"_p{shown}" : "") + ".png");

        File.WriteAllBytes(path, ImageCodec.RgbaToPng(image.ToRgba(shown), image.Width, image.Height));
        return path;
    }

    /// <summary>Every menu screen as a PNG, using the same palette number throughout.</summary>
    public static string ExportMenuImages(RomSession session, IEnumerable<int> assetIds, string folder,
                                          int palette = -1)
    {
        int written = 0, failed = 0, fellBack = 0;

        foreach (int id in assetIds)
        {
            try
            {
                int use = palette;
                if (use >= 0 && session.TryGetAsset(id, out var data)
                    && MenuImage.TryParse(data, out var image) && use >= image!.Palettes.Count)
                {
                    use = -1;
                    fellBack++;
                }

                ExportMenuImagePng(session, id, folder, use);
                written++;
            }
            catch (Exception) { failed++; }
        }

        return $"wrote {written} PNGs to {folder}" +
               (palette >= 0 ? $" using palette {palette}" : "") +
               (fellBack > 0 ? $", {fellBack} of them with fewer palettes than that" : "") +
               (failed > 0 ? $", {failed} failed" : "");
    }

    /// <summary>
    /// One palette as a strip of pixels, one colour wide each, in the order the game reads them.
    /// </summary>
    public static string ExportMenuPalettePng(RomSession session, int assetId, string folder, int palette)
    {
        if (!session.TryGetAsset(assetId, out var data) || !MenuImage.TryParse(data, out var image))
            throw new InvalidDataException($"Asset {assetId} is not a menu screen.");

        Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, $"menu{assetId}_palette{palette}.png");
        File.WriteAllBytes(path, ImageCodec.RgbaToPng(image!.PaletteToRgba(palette), image.Colours, 1));
        return path;
    }

    /// <summary>Replaces one palette from such a strip, leaving the picture's indices alone.</summary>
    public static string ImportMenuPalette(RomSession session, string project, int assetId,
                                           string pngPath, int palette)
    {
        if (!session.TryGetAsset(assetId, out var data) || !MenuImage.TryParse(data, out var image))
            throw new InvalidDataException($"Asset {assetId} is not a menu screen.");

        using var loaded = Image.Load<Rgba32>(pngPath);

        if (loaded.Width != image!.Colours || loaded.Height != 1)
            throw new InvalidDataException(
                $"A palette strip is {image.Colours}x1, but this image is {loaded.Width}x{loaded.Height}.");

        var rgba = new byte[loaded.Width * 4];
        loaded.CopyPixelDataTo(rgba);

        var before = image.PaletteToRgba(palette);
        int changed = 0;
        for (int i = 0; i < image.Colours; i++)
            if (before[i * 4] != rgba[i * 4] || before[i * 4 + 1] != rgba[i * 4 + 1] ||
                before[i * 4 + 2] != rgba[i * 4 + 2] || before[i * 4 + 3] != rgba[i * 4 + 3])
                changed++;

        var replaced = image.WithPalette(palette, rgba);
        string file = WriteProjectAsset(project, assetId, replaced.Write());

        return $"menu screen {assetId}: palette {palette} replaced, {changed} of {image.Colours} " +
               $"colours changed\nthe picture is untouched -- only what its indices resolve to\n" +
               $"wrote {file} -- now Build ROM on the Project tab";
    }

    /// <summary>Replaces a menu screen from a PNG.</summary>
    public static string ImportMenuImage(RomSession session, string project, int assetId, string pngPath,
                                         int palette = -1)
    {
        if (!session.TryGetAsset(assetId, out var data) || !MenuImage.TryParse(data, out var image))
            throw new InvalidDataException($"Asset {assetId} is not a menu screen.");

        using var loaded = Image.Load<Rgba32>(pngPath);

        if (loaded.Width != image!.Width || loaded.Height != image.Height)
            throw new InvalidDataException(
                $"The image is {loaded.Width}x{loaded.Height}, but this screen must be exactly " +
                $"{image.Width}x{image.Height}.");

        var rgba = new byte[loaded.Width * loaded.Height * 4];
        loaded.CopyPixelDataTo(rgba);

        int used = palette < 0 ? image.DisplayPalette : Math.Min(palette, image.Palettes.Count - 1);

        var fit = FitMenuToOriginal(session, assetId, colours => image.WithPixels(
            colours >= FullColour ? rgba : MenuPaletteBuilder.Reduce(rgba, colours),
            loaded.Width, loaded.Height, used));

        string file = WriteProjectAsset(project, assetId, fit.Bytes);

        return $"menu screen {assetId}: colours matched against palette {used} of " +
               $"{image.Palettes.Count}, which is the one the Menus tab was showing\n" +
               fit.Report + "\n" +
               $"wrote {file} -- now Build ROM on the Project tab";
    }

    public const int FullColour = int.MaxValue;

    public sealed record MenuFit(MenuImage Image, byte[] Bytes, int Colours, string Report);

    /// <summary>
    /// Makes an edited menu screen no larger in the ROM than the original, the way backgrounds are
    /// fitted by lowering JPEG quality.
    /// </summary>
    public static MenuFit FitMenuToOriginal(RomSession session, int assetId, Func<int, MenuImage> make)
    {
        session.TryGetCartAsset(assetId, out var cart, out int budget);

        var full = make(FullColour);
        var fullBytes = full.Write();

        if (fullBytes.AsSpan().SequenceEqual(cart))
            return new MenuFit(full, fullBytes, FullColour,
                $"identical to the original -- the rebuild keeps its {budget:N0} bytes");

        int fullSize = Zlib.Compress(fullBytes).Length;
        if (fullSize <= budget)
            return new MenuFit(full, fullBytes, FullColour,
                $"{fullSize:N0} bytes compressed, within the original {budget:N0} -- all colours kept");

        MenuFit? best = null;
        int bestSize = 0;
        int low = 2, high = 255;
        while (low <= high)
        {
            int colours = (low + high) / 2;
            var image = make(colours);
            var bytes = image.Write();
            int size = Zlib.Compress(bytes).Length;

            if (size <= budget)
            {
                best = new MenuFit(image, bytes, colours, "");
                bestSize = size;
                low = colours + 1;
            }
            else high = colours - 1;
        }

        if (best is not null)
            return best with
            {
                Report = $"{fullSize:N0} bytes compressed with every colour, more than the original " +
                         $"{budget:N0} -- reduced to {best.Colours} colours, {bestSize:N0} bytes"
            };

        var least = make(2);
        var leastBytes = least.Write();
        int leastSize = Zlib.Compress(leastBytes).Length;
        return new MenuFit(least, leastBytes, 2,
            $"does not fit the original {budget:N0} bytes even at 2 colours ({leastSize:N0} bytes) -- " +
            "kept at 2 colours; the rebuild allows it but it takes from the shared region");
    }

    /// <summary>Replaces a menu screen together with a palette built from the picture itself.</summary>
    public static (string Log, string AdjustedPath) ImportMenuImageWithNewPalette(
        RomSession session, string project, int assetId, string pngPath, int palette = -1)
    {
        if (string.IsNullOrWhiteSpace(pngPath))
            throw new InvalidOperationException("Choose an image in \"PNG to import\" first.");

        if (!session.TryGetAsset(assetId, out var data) || !MenuImage.TryParse(data, out var image))
            throw new InvalidDataException($"Asset {assetId} is not a menu screen.");

        using var loaded = Image.Load<Rgba32>(pngPath);

        if (loaded.Width != image!.Width || loaded.Height != image.Height)
            throw new InvalidDataException(
                $"The image is {loaded.Width}x{loaded.Height}, but this screen must be exactly " +
                $"{image.Width}x{image.Height}.");

        var rgba = new byte[loaded.Width * loaded.Height * 4];
        loaded.CopyPixelDataTo(rgba);

        int used = palette < 0 ? image.DisplayPalette : Math.Min(palette, image.Palettes.Count - 1);

        var fit = FitMenuToOriginal(session, assetId, colours =>
            image.WithPalette(used, MenuPaletteBuilder.Build(rgba, image.Colours, colours).PaletteRgba)
                 .WithPixels(rgba, loaded.Width, loaded.Height, used));

        var built = MenuPaletteBuilder.Build(rgba, image.Colours, fit.Colours);
        var replaced = fit.Image;

        // Beside the source, named after it; importing an adjusted picture again does not stack suffixes.
        string folder = Path.GetDirectoryName(Path.GetFullPath(pngPath))!;
        string stem = Path.GetFileNameWithoutExtension(pngPath);
        if (stem.EndsWith("_adjusted", StringComparison.OrdinalIgnoreCase)) stem = stem[..^"_adjusted".Length];

        // "_newpal" is the palette itself -- the 256x1 strip -- and "_adjusted" the picture matched into it.
        string palettePath = Path.Combine(folder, $"{stem}_newpal.png");
        string adjustedPath = Path.Combine(folder, $"{stem}_adjusted.png");

        File.WriteAllBytes(palettePath, ImageCodec.RgbaToPng(replaced.PaletteToRgba(used), image.Colours, 1));
        File.WriteAllBytes(adjustedPath, ImageCodec.RgbaToPng(replaced.ToRgba(used), image.Width, image.Height));

        var bytes = fit.Bytes;
        string file = WriteProjectAsset(project, assetId, bytes);

        var log = new System.Text.StringBuilder();
        log.AppendLine($"menu screen {assetId}: new palette {used} built from {Path.GetFileName(pngPath)}");
        log.AppendLine(built.Merged
            ? $"the picture has {built.DistinctColours:N0} colours (counted at the game's 5 bits per channel) " +
              $"-- similar colours were merged into {built.PaletteEntries}"
            : $"the picture has {built.DistinctColours:N0} colours (counted at the game's 5 bits per channel), " +
              $"all kept exactly");
        log.AppendLine(fit.Report);
        if (built.HasTransparency) log.AppendLine("transparent pixels use entry 0");
        if (image.Palettes.Count > 1)
            log.AppendLine($"note: this screen has {image.Palettes.Count} palettes and only palette {used} was " +
                           "replaced -- the others now read the new indices with their old colours");
        log.AppendLine($"wrote new palette ({image.Colours}x1) {palettePath}");
        log.AppendLine($"wrote adjusted picture {adjustedPath}");
        log.Append($"imported both into {file} -- now Build ROM on the Project tab");

        return (log.ToString(), adjustedPath);
    }

    // ---- unused textures --------------------------------------------------

    /// <summary>
    /// Blanks textures no model draws any more, to get the asset region back under its limit.
    /// </summary>
    public static string ReclaimUnusedTextures(RomSession session, string project, bool apply)
    {
        if (!HasProject(project))
            throw new FileNotFoundException("No extracted project. Use Extract on the Project tab first.");

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(
                           SharedFile.ReadAllText(Path.Combine(project, ProjectFolder.ManifestName)))
                       ?? throw new InvalidDataException("manifest.json could not be read.");

        byte[]? MeshBytes(int assetId)
        {
            var blob = manifest.Blobs.FirstOrDefault(b => b.Ids.Contains(assetId));
            if (blob is null) return null;

            string file = Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file)) return SharedFile.ReadAllBytes(file);

            return session.TryGetAsset(assetId, out var data) ? data : null;
        }

        var report = TextureUsage.Analyse(session.Rom, MeshBytes);

        long before = 0, after = 0;
        int written = 0, skipped = 0;

        foreach (int id in report.Dead)
        {
            var blob = manifest.Blobs.FirstOrDefault(b => b.Ids.Contains(id));
            if (blob is null) { skipped++; continue; }

            // A blob two ids share cannot be blanked for one of them alone.
            if (blob.Ids.Count > 1 && blob.Ids.Any(other => report.Live.Contains(other))) { skipped++; continue; }

            if (!session.TryGetAsset(id, out var current) || !TextureFile.TryParse(current, out var texture))
            {
                skipped++;
                continue;
            }

            var blank = TextureUsage.Blank(texture!);

            before += blob.StoredSize;
            after += Zlib.Compress(blank).Length;
            written++;

            if (apply) WriteProjectAsset(project, id, blank);
        }

        string verb = apply ? "blanked" : "would blank";

        return $"{report.Dead.Count} textures are no longer drawn by any model " +
               $"({report.Live.Count} still are, of {report.Listed.Count} listed)\n" +
               $"{verb} {written}" + (skipped > 0 ? $", left {skipped} alone" : "") +
               $" -- about {before - after:N0} bytes recovered" +
               (report.ModelsUnreadable > 0
                   ? $"\n{report.ModelsUnreadable} meshes would not parse and were treated as still needing everything they list"
                   : "") +
               (apply ? "\nnow Build ROM on the Project tab" : "");
    }

    // ---- foregrounds ------------------------------------------------------

    /// <summary>Replaces the foreground shape for a background from a PNG.</summary>
    public static string ImportForeground(RomSession session, string project, int backgroundIndex, string pngPath)
    {
        int assetId = session.MaskAssetForBackground(backgroundIndex);
        if (assetId < 0)
            throw new InvalidDataException(
                $"bg{backgroundIndex:D4} has no foreground, so there is nothing to replace. " +
                "Only a camera the game already gave one can have its shape edited.");

        var mask = session.MaskForBackground(backgroundIndex)
                   ?? throw new InvalidDataException($"The foreground for bg{backgroundIndex:D4} (asset {assetId}) will not parse.");

        using var image = Image.Load<Rgba32>(pngPath);

        if (image.Width != mask.Width || image.Height != mask.Height)
            throw new InvalidDataException(
                $"The image is {image.Width}x{image.Height}, but a foreground must be exactly " +
                $"{mask.Width}x{mask.Height}.");

        var screen = new bool[mask.Width * mask.Height];
        int asked = 0;

        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    // Half-transparent counts as covered: the format has no partial coverage, so the
                    // choice is only where to put the line.
                    if (row[x].A < 128) continue;

                    screen[y * mask.Width + x] = true;
                    asked++;
                }
            }
        });

        var replaced = mask.WithCoverage(screen, out int outside, out int untouched);
        var bytes = replaced.Write();

        string file = WriteProjectAsset(project, assetId, bytes);

        string report = $"bg{backgroundIndex:D4} foreground (asset {assetId}): " +
                        $"{asked - outside:N0} of {asked:N0} painted pixels kept, {bytes.Length:N0} bytes";

        if (outside > 0)
            report += $"\n{outside:N0} pixels fell outside every existing piece and were dropped -- " +
                      "the shape can be changed within the areas the game already draws, but new ones " +
                      "cannot be invented, because nothing in the image says how near the camera they are";

        if (untouched > 0)
            report += $"\n{untouched} piece(s) carry their own image and were left alone -- " +
                      "every pixel of those draws, so they have no shape to replace";

        return report + $"\nwrote {file} -- now Build ROM on the Project tab";
    }

    // ---- sound ------------------------------------------------------------

    /// <summary>Replaces one sample in a project folder from a WAV.</summary>
    public static string ImportSound(RomSession session, string project, int sampleIndex, string wavPath)
        => ImportSounds(session, project, new[] { sampleIndex }, wavPath);

    /// <summary>
    /// Replaces every listed sample with the same WAV, in one rebuild of the bank. Each copy is
    /// brought to its own slot's rate, so the one file plays at the right pitch everywhere.
    /// </summary>
    public static string ImportSounds(RomSession session, string project, IReadOnlyList<int> sampleIndices,
                                      string wavPath)
    {
        var targets = sampleIndices.Distinct().ToList();
        if (targets.Count == 0) throw new ArgumentException("No samples to import into.");

        // The project's own bank files are the truth. The session's view catches up only when a
        // background rescan finishes, so two imports in quick succession would otherwise both start
        // from the bank before either, and the second would write the first's sample back out.
        var layout = session.Rom.Layout;
        var bank = ReadProjectAsset(project, layout.SampleDirectoryAsset) is { } onDiskTable &&
                   ReadProjectAsset(project, layout.SampleDataAsset) is { } onDiskData
            ? SoundDirectory.ReadFrom(onDiskTable, onDiskData)
            : session.Sounds;

        foreach (int index in targets)
            if (bank.Samples.All(s => s.Index != index))
                throw new InvalidDataException($"Sample {index} is not in the bank.");

        var (pcm, rate) = WavCodec.Read(File.ReadAllBytes(wavPath));
        var replacements = targets.Select(i => new SoundBankBuilder.Replacement(i, pcm, rate)).ToList();

        // WAVs edited by hand in the project and not yet folded into the bank go in with these.
        // Left pending, they would make the next scan and Build rebuild the bank from the cart plus
        // those WAVs alone, which throws away every sample brought in through Import.
        var pending = PendingSoundWavs(project, targets.ToHashSet());
        replacements.AddRange(pending.Select(p => p.Replacement));

        // Rebuilt from what the bank holds now, so other samples keep their edits, but the rate each
        // import is brought back to comes from the cart -- otherwise replacing an already-replaced
        // sample measures itself against the previous import and keeps its rate.
        var notes = new List<string>();
        var (table, data) = SoundBankBuilder.Rebuild(bank, replacements, notes.Add, session.CartSounds);

        WriteProjectAsset(project, layout.SampleDirectoryAsset, table);
        string file = WriteProjectAsset(project, layout.SampleDataAsset, data);

        UpdateProjectWavs(project, targets, wavPath);
        MarkSoundWavsApplied(project, pending.Select(p => p.Index));

        // What the bank now holds, rather than what the WAV was: the two differ whenever the audio
        // had to be resampled, and the stored rate is the one that decides how it plays.
        var rebuilt = SoundDirectory.ReadFrom(table, data);
        double seconds = rate > 0 ? pcm.Length / (double)rate : 0;
        string source = $"source WAV {pcm.Length:N0} samples at {rate} Hz ({seconds:0.00}s)";

        string body;
        if (targets.Count == 1)
        {
            int index = targets[0];
            var sample = bank.Samples.First(s => s.Index == index);
            var cartSample = session.CartSounds.Samples.FirstOrDefault(s => s.Index == index) ?? sample;
            var stored = rebuilt.Samples.First(s => s.Index == index);
            body = $"sample {index}: {stored.DeclaredLength:N0} samples at {stored.SampleRate} Hz " +
                   $"({stored.Seconds:0.00}s), was {sample.Seconds:0.00}s at {sample.SampleRate} Hz " +
                   $"(the ROM's own: {cartSample.Seconds:0.00}s at {cartSample.SampleRate} Hz); {source}\n" +
                   string.Concat(notes.Select(n => n + "\n"));
        }
        else
        {
            // One line per rate rather than per sample: a long selection would otherwise bury the
            // outcome under dozens of near-identical resampling notes.
            var byRate = rebuilt.Samples.Where(s => targets.Contains(s.Index))
                .GroupBy(s => s.SampleRate).OrderBy(g => g.Key)
                .Select(g => $"  {g.Count()} at {g.Key:N0} Hz ({g.First().Seconds:0.00}s): " +
                             string.Join(", ", g.Select(s => s.Index)));
            long grew = (long)data.Length - bank.SampleData.Length;
            body = $"{targets.Count} samples replaced with the same audio; {source}\n" +
                   string.Concat(byRate.Select(l => l + "\n")) +
                   $"sample data {(grew >= 0 ? "grew" : "shrank")} by {Math.Abs(grew):N0} bytes " +
                   $"to {data.Length:N0}\n";
        }

        return body +
               (pending.Count > 0
                   ? $"also folded in {pending.Count} WAV(s) edited in the project folder: " +
                     string.Join(", ", pending.Select(p => p.Index)) + "\n"
                   : "") +
               $"wrote {file} -- now Build ROM on the Project tab";
    }

    /// <summary>Project WAVs whose contents no longer match the hash the manifest recorded.</summary>
    private static List<(int Index, SoundBankBuilder.Replacement Replacement)> PendingSoundWavs(
        string project, IReadOnlySet<int> except)
    {
        var result = new List<(int, SoundBankBuilder.Replacement)>();
        try
        {
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(
                SharedFile.ReadAllText(Path.Combine(project, ProjectFolder.ManifestName)));
            if (manifest is null) return result;

            foreach (var sample in manifest.SoundSamples)
            {
                if (except.Contains(sample.Index)) continue;
                string path = Path.Combine(project, sample.File.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;

                byte[] wav = SharedFile.ReadAllBytes(path);
                if (System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wav)) == sample.Sha256)
                    continue;

                try
                {
                    var (pcm, rate) = WavCodec.Read(wav);
                    result.Add((sample.Index, new SoundBankBuilder.Replacement(sample.Index, pcm, rate)));
                }
                catch (Exception)
                {
                    // Unreadable: Build leaves it out too, so it is not an edit to carry.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // No manifest to compare against: nothing counts as pending.
        }
        return result;
    }

    /// <summary>Re-records the WAV hashes of samples that are now in the bank.</summary>
    private static void MarkSoundWavsApplied(string project, IEnumerable<int> indices)
    {
        var set = indices.ToHashSet();
        if (set.Count == 0) return;
        try
        {
            string manifestPath = Path.Combine(project, ProjectFolder.ManifestName);
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(SharedFile.ReadAllText(manifestPath));
            if (manifest is null) return;

            foreach (var sample in manifest.SoundSamples.Where(x => set.Contains(x.Index)))
            {
                string path = Path.Combine(project, sample.File.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                    sample.Sha256 = System.Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(SharedFile.ReadAllBytes(path)));
            }

            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // The bank already holds them; a stale hash only means the next Build folds them again.
        }
    }

    /// <summary>
    /// Copies the imported audio over the project's own WAV for each sample, and re-records their
    /// hashes so the rebuild treats them as already applied rather than as fresh edits.
    /// </summary>
    private static void UpdateProjectWavs(string project, IReadOnlyCollection<int> sampleIndices, string wavPath)
    {
        try
        {
            string manifestPath = Path.Combine(project, ProjectFolder.ManifestName);
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(SharedFile.ReadAllText(manifestPath));
            if (manifest is null) return;

            byte[] wav = File.ReadAllBytes(wavPath);
            string hash = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wav));

            foreach (var entry in manifest.SoundSamples.Where(s => sampleIndices.Contains(s.Index)))
            {
                string target = Path.Combine(project, entry.File.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, wav);
                entry.Sha256 = hash;
            }

            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // The bank is already written; a stale WAV copy is untidy, not wrong.
        }
    }

    // ---- voice ------------------------------------------------------------

    /// <summary>Writes one decoded dialogue clip as a WAV.</summary>
    public static string ExportVoiceWav(RomSession session, string folder, VoiceClip clip)
    {
        Directory.CreateDirectory(folder);

        var samples = session.DecodeVoice(clip);
        string path = Path.Combine(folder, $"voice{clip.Index:D4}_{clip.SampleRate}hz.wav");
        File.WriteAllBytes(path, WavCodec.Write(samples, clip.SampleRate));

        return $"exported {Path.GetFileName(path)} ({clip.Seconds:0.00}s)";
    }

    /// <summary>Writes a clip's encoded bytes, exactly as the cart stores them.</summary>
    public static string ExportVoiceRaw(RomSession session, string folder, VoiceClip clip)
    {
        Directory.CreateDirectory(folder);

        var bank = session.VoiceBankData;
        string path = Path.Combine(folder, $"voice{clip.Index:D4}.mort");
        File.WriteAllBytes(path, bank.AsSpan(clip.Offset, clip.StoredSize).ToArray());

        return $"exported {Path.GetFileName(path)} ({clip.StoredSize:N0} bytes, encoded)";
    }

    /// <summary>Puts an encoded clip into a slot, in place.</summary>
    public static string ImportVoiceRaw(RomSession session, string project, VoiceClip clip, string path)
    {
        var replacement = SharedFile.ReadAllBytes(path);

        int blocks = MortCodec.ReadBlockCount(replacement);
        int rate = MortCodec.ReadSampleRate(replacement);

        var rebuilt = VoiceBank.ReplaceInPlace(session.VoiceBankData, clip, replacement,
                                               (c, from, to) => MortCodec.PadWithSilence(session.VoiceCpu, c, from, to));

        string file = WriteProjectAsset(project, session.Rom.Layout.VoiceBankAsset, rebuilt);

        double seconds = blocks * (double)VoiceClip.SamplesPerBlock / Math.Max(1, rate);
        string padding = blocks < clip.BlockCount
            ? $", padded with {(clip.Seconds - seconds):0.00}s of silence to fill the slot"
            : "";

        return $"clip {clip.Index} replaced with {Path.GetFileName(path)} " +
               $"({seconds:0.00}s at {rate} Hz{padding}); wrote {file}";
    }

    /// <summary>Replaces a clip with new audio from a WAV, encoding it into the MORT codec.</summary>
    public static string ImportVoiceWav(RomSession session, string project, VoiceClip clip, string path)
    {
        var (pcm, rate) = WavCodec.Read(SharedFile.ReadAllBytes(path));
        if (pcm.Length == 0) throw new InvalidDataException($"'{Path.GetFileName(path)}' holds no audio.");

        // The slot declares the rate the console plays it back at, so the audio is brought to it.
        string resampled = "";
        if (rate != clip.SampleRate && clip.SampleRate > 0)
        {
            int before = pcm.Length;
            pcm = SoundBankBuilder.Resample(pcm, rate, clip.SampleRate);
            resampled = $"resampled {rate:N0} Hz -> {clip.SampleRate:N0} Hz " +
                        $"({before:N0} -> {pcm.Length:N0} samples). ";
            rate = clip.SampleRate;
        }

        var encoded = MortEncoder.EncodeToFit(pcm, rate, clip.StoredSize, clip.BlockCount,
                                              out var report, out string note);

        if (encoded.Length > clip.StoredSize)
            throw new InvalidDataException(
                $"The encoded clip is {encoded.Length:N0} bytes but slot {clip.Index} holds " +
                $"{clip.StoredSize:N0}. Voice slots cannot grow -- the bank is addressed by offset. " +
                "Try shorter or quieter audio.");

        var rebuilt = VoiceBank.ReplaceInPlace(session.VoiceBankData, clip, encoded,
                                               (c, from, to) => MortCodec.PadWithSilence(session.VoiceCpu, c, from, to));

        string file = WriteProjectAsset(project, session.Rom.Layout.VoiceBankAsset, rebuilt);

        return $"clip {clip.Index} replaced with {Path.GetFileName(path)}" + Environment.NewLine +
               resampled + note +
               $"encoded {report.Blocks:N0} blocks ({report.SilentBlocks:N0} silent), " +
               $"{encoded.Length:N0} of the slot's {clip.StoredSize:N0} bytes, " +
               $"{report.SignalToNoise:0.0} dB against the source" + Environment.NewLine +
               $"wrote {file} -- now Build ROM on the Project tab";
    }

    // ---- movies -----------------------------------------------------------

    /// <summary>Writes a movie out as a plain MPEG-1 elementary stream.</summary>
    public static string ExportMovie(RomSession session, string folder, FmvStream movie)
    {
        Directory.CreateDirectory(folder);

        string name = movie.Name.Length > 0
            ? Path.GetFileNameWithoutExtension(movie.Name)
            : $"fmv{movie.AssetId:D4}";

        string path = Path.Combine(folder, $"{name}.m2v");
        File.WriteAllBytes(path, session.MovieData(movie));

        return $"exported {Path.GetFileName(path)} ({movie.Width}x{movie.Height}, " +
               $"{movie.Pictures:N0} frames, {movie.Seconds:0.00}s, {movie.StoredSize:N0} bytes)";
    }

    /// <summary>Replaces a movie, converting from whatever was supplied.</summary>
    public static string ImportMovie(RomSession session, string project, FmvStream movie, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("No such file: " + path);

        // The movie keeps the name it had.
        int nameCost = FmvEncoder.NameBlockLength(movie.Name);
        int budget = movie.StoredSize;

        byte[] stream;
        string how;

        if (FmvEncoder.IsElementaryStream(path))
        {
            stream = SharedFile.ReadAllBytes(path);

            if (FmvEncoder.Reject(stream) is { } reason) throw new InvalidDataException(reason);

            // Cut to the slot's length, at a group boundary so nothing is left referring to frames that
            // are no longer there.
            int before = FmvIndex.Parse(movie.AssetId, stream)?.Pictures ?? 0;
            stream = FmvEncoder.TruncateToPictures(stream, movie.Pictures, out int kept);

            how = before > kept
                ? $"taken as-is and cut from {before:N0} to {kept:N0} frames, {stream.Length:N0} bytes"
                : $"taken as-is, {stream.Length:N0} bytes";
        }
        else
        {
            if (!Ffmpeg.Available) throw new InvalidOperationException(Ffmpeg.Missing);

            string scratch = Path.Combine(Path.GetTempPath(), "re2-fmv-" + Guid.NewGuid().ToString("N"));

            try
            {
                // Length first, quality second: the conversion is cut to the slot's duration, and only
                // then is the bit rate searched.
                var result = FmvEncoder.EncodeToFit(path, budget - nameCost, scratch, movie.Seconds);

                if (result.Data.Length == 0)
                    throw new InvalidDataException("ffmpeg could not convert that file:"
                                                   + Environment.NewLine + result.Log);

                stream = result.Data;

                if (FmvEncoder.Reject(stream) is { } reason)
                    throw new InvalidDataException("The converted stream is not one the game can " +
                                                   "play: " + reason);

                int frames = FmvIndex.Parse(movie.AssetId, stream)?.Pictures ?? 0;
                how = $"converted at {result.Kilobits} kbit/s, {stream.Length:N0} bytes" +
                      (frames < movie.Pictures
                          ? $"; cut to the slot's {movie.Seconds:0.00}s"
                          : "");
            }
            finally
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
                catch (IOException) { /* scratch */ }
            }
        }

        // Whatever produced it, the stream is made to look like the ones that are known to play:
        // the slot's own name, and the rate fields the retail movies declare.
        stream = FmvEncoder.WithName(stream, movie.Name);
        FmvEncoder.Conform(stream);

        string fit = stream.Length <= budget
            ? $"{budget - stream.Length:N0} bytes under the original {budget:N0}"
            : $"{stream.Length - budget:N0} bytes OVER the original {budget:N0}, which the rebuild " +
              "allows but takes from the shared region";

        var parsed = FmvIndex.Parse(movie.AssetId, stream);
        string shape = parsed is null ? "" :
            $"{parsed.Pictures:N0} frames, {parsed.Seconds:0.00}s (was {movie.Pictures:N0} frames, " +
            $"{movie.Seconds:0.00}s)" + Environment.NewLine;

        Rehome(session, project, movie.AssetId, stream);
        string file = WriteProjectAsset(project, movie.AssetId, stream);

        return $"movie {movie.AssetId} ({movie.Name}) replaced with {Path.GetFileName(path)}"
               + Environment.NewLine
               + shape + how + ", " + fit + Environment.NewLine
               + $"wrote {file} -- now Build ROM on the Project tab";
    }

    // ---- models -----------------------------------------------------------

    /// <summary>
    /// Writes a character out as glTF, with its textures, and its skeleton and clips when it has them.
    /// </summary>
    public static string ExportCharacterGltf(RomSession session, ModelEntry character, string folder,
                                             bool withAnimation, bool withRig = false)
    {
        var mesh = session.LoadMesh(character.MeshAssetId)
                   ?? throw new InvalidDataException($"Asset {character.MeshAssetId} is not a model.");

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"mesh{character.MeshAssetId}.glb");

        var textures = CollectTextures(session, character);
        bool usable = textures.Any(t => t.Png.Length > 0);

        // withRig keeps the skeleton and skin without the clips.
        PoseBank? bank = withAnimation || withRig ? session.LoadPoseBank(character) : null;
        AnimationSet? clips = withAnimation ? session.LoadAnimations(character) : null;

        if (bank is not null)
            AnimatedGltfExporter.Save(mesh, bank, clips, usable ? textures : null, path,
                                      $"char{character.MeshAssetId}");
        else
            GltfExporter.Save(mesh, usable ? textures : null, path, $"char{character.MeshAssetId}");

        string what = bank is null
            ? "no rig, static mesh"
            : $"{bank.PartCount} joints, {clips?.Clips.Count ?? 0} clips";

        return $"wrote {path}  ({mesh.PartCount} parts, {mesh.TotalTriangles:N0} tris, {what})";
    }

    public static string ExportCharacters(RomSession session, IEnumerable<ModelEntry> characters,
                                          string folder, bool withAnimation, bool withRig = false)
    {
        int written = 0, failed = 0;
        string? last = null;

        foreach (var character in characters)
        {
            try { last = ExportCharacterGltf(session, character, folder, withAnimation, withRig); written++; }
            catch (Exception) { failed++; }
        }

        return written == 1 && failed == 0
            ? last!
            : $"wrote {written:N0} models to {folder}" + (failed > 0 ? $" ({failed:N0} failed)" : "");
    }

    /// <summary>
    /// Keeps the texture list index-aligned with the character's texture ids: the mesh refers to them
    /// by position, so a texture that will not decode has to leave a hole rather than be skipped.
    /// </summary>
    private static List<ExportTexture> CollectTextures(RomSession session, ModelEntry character)
    {
        var textures = new List<ExportTexture>();

        foreach (int id in character.Textures.TextureIds)
        {
            if (session.TryGetAsset(id, out var data) && TextureFile.TryParse(data, out var texture))
                textures.Add(new ExportTexture(
                    ImageCodec.RgbaToPng(texture.ToRgba(), texture.Width, texture.Height),
                    texture.Width, texture.Height));
            else
                textures.Add(new ExportTexture(Array.Empty<byte>(), 1, 1));
        }

        return textures;
    }

    /// <summary>Replaces a character's geometry in a project folder from a glTF or GLB.</summary>
    public static string ImportMesh(RomSession session, string project, ModelEntry character, string glbPath)
    {
        int assetId = character.MeshAssetId;
        var existing = session.LoadMesh(assetId)
                       ?? throw new InvalidDataException($"Asset {assetId} is not a model.");

        IReadOnlyList<(int X, int Y, int Z)>? offsets = null;
        string restNote = "parts taken as authored";

        PoseBank? bank = null;
        IReadOnlyList<(int X, int Y, int Z)>? editedRest = null;

        try { bank = session.LoadPoseBank(character); }
        catch (Exception) { /* no rig: the parts stand on their own */ }

        if (bank is not null)
        {
            offsets = bank.RestWorldPositions();
            restNote = "rest pose applied";

            // A skeleton edited in a modelling tool comes back as changed joint offsets.
            editedRest = ReadEditedRestPose(bank, glbPath);
            if (editedRest is not null)
            {
                offsets = bank.WorldPositionsFrom(editedRest);
                restNote = "skeleton moved, rest pose updated";
            }
        }

        var (model, report) = GltfImporter.Load(glbPath, new GltfImporter.Options
        {
            PartOffsets = offsets,
            PartCount = existing.PartCount,
            TextureCount = existing.TextureCount
        });

        // Refused rather than written: no model in the game leaves a part empty -- all 355 populate
        // every part they declare -- and one that does crashes as it comes on screen.
        var starved = Enumerable.Range(0, model.Parts.Count)
                                .Where(i => model.Parts[i].SubMeshes.Count == 0)
                                .ToList();

        if (starved.Count > 0)
            throw new InvalidDataException(
                $"Part(s) {string.Join(", ", starved)} would have no geometry, and the game crashes on " +
                $"a model like that as it appears. Mesh {assetId} has {existing.PartCount} parts and " +
                "every one needs geometry weighted to its bone. Bone N in the file fills part N, so " +
                "starting from the exported model keeps the numbering right.");

        var bytes = MeshWriter.Write(model);
        var check = MeshFile.Parse(bytes);                 // fail here rather than in the game
        string file = WriteProjectAsset(project, assetId, bytes);

        string textureNote = ImportMeshTextures(session, project, character, existing.TextureCount,
                                                glbPath, out var textureWarnings);

        var skeletonMessages = new List<string>();
        string skeletonNote = "skeleton: unchanged";

        if (editedRest is not null)
        {
            skeletonNote = WriteRestPose(session, project, character, bank!, editedRest, out skeletonMessages);
        }

        var allWarnings = report.Warnings.Concat(textureWarnings).Concat(skeletonMessages).Take(6).ToList();
        string warnings = allWarnings.Count == 0
            ? ""
            : "\n" + string.Join("\n", allWarnings.Select(w => "warning: " + w));

        return $"mesh {assetId}: {report.Parts} parts ({report.PartsMatchedByName} matched by name), " +
               $"{report.Vertices:N0} verts, {report.Triangles:N0} tris, {restNote}\n" +
               $"re-read gives {check.PartCount} parts, {check.TotalTriangles:N0} tris\n" +
               $"{textureNote}\n{skeletonNote}\n" +
               $"wrote {file} -- now Build ROM on the Project tab" + warnings;
    }

    /// <summary>The file's joint offsets, but only when they actually differ from the ROM's.</summary>
    private static IReadOnlyList<(int X, int Y, int Z)>? ReadEditedRestPose(PoseBank bank, string glbPath)
    {
        Dictionary<int, (int X, int Y, int Z)> fromFile;
        try { fromFile = GltfImporter.LoadRestPose(glbPath, bank.PartCount); }
        catch (Exception) { return null; }

        if (fromFile.Count == 0) return null;

        var offsets = new (int X, int Y, int Z)[bank.PartCount];
        bool changed = false;

        for (int part = 0; part < bank.PartCount; part++)
        {
            var joint = bank.Joints[part];
            var current = ((int)joint.X, (int)joint.Y, (int)joint.Z);

            // A joint the file does not mention keeps whatever the ROM has.
            offsets[part] = fromFile.TryGetValue(part, out var edited) ? edited : current;
            if (offsets[part] != current) changed = true;
        }

        return changed ? offsets : null;
    }

    /// <summary>Writes an edited skeleton back into the project.</summary>
    private static string WriteRestPose(RomSession session, string project, ModelEntry character,
                                        PoseBank bank, IReadOnlyList<(int X, int Y, int Z)> offsets,
                                        out List<string> warnings)
    {
        warnings = new List<string>();

        try
        {
            int clamped = PoseBankWriter.WriteRestOffsets(bank, offsets);
            if (clamped > 0)
                warnings.Add($"{clamped} joint offset(s) were too large for the game's 16-bit fields " +
                             "and were clamped; the model is probably at the wrong scale");

            int indexAssetId = character.AnimationAssetIds[1];
            var chunks = PoseBankWriter.Layout(session.Rom, session.Assets, indexAssetId);
            var split = PoseBankWriter.SplitToAssets(bank.Data, chunks, out var splitWarnings);
            warnings.AddRange(splitWarnings);

            int changed = 0;
            foreach (var (assetId, bytes) in split)
            {
                var entry = session.Assets.Entries.FirstOrDefault(e => e.Index == assetId);
                if (entry is not null && session.Assets.TryGetData(session.Rom, entry, out var before)
                    && before.AsSpan().SequenceEqual(bytes))
                    continue;

                WriteProjectAsset(project, assetId, bytes);
                changed++;
            }

            return $"skeleton: {offsets.Count} joints written, {changed} asset(s) changed";
        }
        catch (Exception ex)
        {
            warnings.Add("the edited skeleton could not be written: " + ex.Message);
            return "skeleton: not written";
        }
    }

    /// <summary>Writes the glTF's own texture images over the character's textures.</summary>
    private static string ImportMeshTextures(RomSession session, string project, ModelEntry character,
                                             int textureCount, string glbPath, out List<string> warnings)
    {
        warnings = new List<string>();

        Dictionary<int, byte[]> images;
        try { images = GltfImporter.LoadTextureImages(glbPath, textureCount); }
        catch (Exception ex)
        {
            warnings.Add("could not read the file's textures: " + ex.Message);
            return "textures: none read";
        }

        if (images.Count == 0) return "textures: none in the file";

        int written = 0, unchanged = 0, skipped = 0, resized = 0;

        foreach (var (slot, png) in images.OrderBy(pair => pair.Key))
        {
            if (slot >= character.Textures.TextureIds.Count)
            {
                warnings.Add($"material tex{slot:D2} has no matching texture on this character");
                skipped++;
                continue;
            }

            int textureAssetId = character.Textures.TextureIds[slot];

            try
            {
                using var image = Image.Load<Rgba32>(png);
                var (file, report) = ReplaceTexture(session, project, textureAssetId, image, $"tex{slot:D2}");

                if (report.StartsWith("resized", StringComparison.Ordinal)) resized++;
                if (file is null) unchanged++; else written++;
            }
            catch (Exception ex)
            {
                warnings.Add($"tex{slot:D2} (asset {textureAssetId}): {ex.Message}");
                skipped++;
            }
        }

        var parts = new List<string>();
        if (written > 0) parts.Add($"{written} replaced");
        if (resized > 0) parts.Add($"{resized} resized to fit");
        if (unchanged > 0) parts.Add($"{unchanged} unchanged");
        if (skipped > 0) parts.Add($"{skipped} skipped");

        return "textures: " + (parts.Count == 0 ? "none" : string.Join(", ", parts));
    }

    /// <summary>Rewrites a character's poses from a rigged glTF.</summary>
    public static string ImportAnimation(RomSession session, string project, ModelEntry character,
                                         string glbPath)
    {
        if (character.AnimationAssetIds.Count < 2)
            throw new InvalidDataException($"Character {character.Index} has no animation assets.");

        int indexAssetId = character.AnimationAssetIds[1];
        var bank = PoseBank.Assemble(session.Rom, session.Assets, indexAssetId);
        var clips = AnimationSet.Decode(session.Rom, session.Assets, character.AnimationAssetIds[0]);

        var report = GltfAnimationImporter.Load(glbPath, bank, clips);

        var chunks = PoseBankWriter.Layout(session.Rom, session.Assets, indexAssetId);
        var split = PoseBankWriter.SplitToAssets(bank.Data, chunks, out var splitWarnings);

        int changed = 0;
        foreach (var (assetId, bytes) in split)
        {
            var entry = session.Assets.Entries.FirstOrDefault(e => e.Index == assetId);
            if (entry is not null && session.Assets.TryGetData(session.Rom, entry, out var before)
                && before.AsSpan().SequenceEqual(bytes))
                continue;                                   // unchanged: leave the file alone

            WriteProjectAsset(project, assetId, bytes);
            changed++;
        }

        var warnings = report.Warnings.Concat(splitWarnings).Take(4).ToList();
        string warningText = warnings.Count == 0 ? "" : "\n" + string.Join("\n", warnings.Select(w => "warning: " + w));

        return $"matched {report.ClipsMatched} clips, sampled {report.FramesSampled:N0} frames, " +
               $"rewrote {report.PosesWritten:N0} poses\n" +
               $"{changed} of {split.Count} pose assets changed -- now Build ROM on the Project tab" +
               warningText;
    }

    // ---- reverting --------------------------------------------------------

    /// <summary>
    /// Puts an asset in a project folder back to what the ROM holds, discarding the edit.
    /// </summary>
    public static string RevertOverride(RomSession session, string project, int assetId)
    {
        string manifestPath = Path.Combine(project, ProjectFolder.ManifestName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("No extracted project to revert into.");

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(SharedFile.ReadAllText(manifestPath))
                       ?? throw new InvalidDataException("Could not read manifest.json.");

        var blob = manifest.Blobs.FirstOrDefault(b => b.Ids.Contains(assetId))
                   ?? throw new InvalidDataException($"Asset {assetId} is not in the project.");

        // A directory of its own, so what is written is the cart's own bytes.
        var directory = AssetDirectory.Read(session.Rom);
        var entry = directory.Entries.FirstOrDefault(e => blob.Ids.Contains(e.Index))
                    ?? throw new InvalidDataException($"Asset {assetId} is not in the ROM.");

        // Decoded when the codec is understood, stored otherwise -- exactly what Extract wrote.
        byte[] original = directory.TryGetData(session.Rom, entry, out var decoded)
            ? decoded
            : session.Rom.Slice(entry.RomOffset, entry.StoredSize).ToArray();

        string path = Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(path, original);

        string note = $"reverted {blob.File} to the ROM ({original.Length:N0} bytes)";

        if (assetId == session.Rom.Layout.SampleDataAsset || assetId == session.Rom.Layout.SampleDirectoryAsset)
        {
            int restored = ProjectFolder.RestoreSoundSamples(session.Rom, project, manifest);
            if (restored > 0)
            {
                File.WriteAllText(manifestPath,
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                note += $", and {restored} edited sample WAV(s)";
            }
        }

        return note;
    }

    // ---- project ----------------------------------------------------------

    /// <summary>Whether a folder holds an extracted project that an import can be written into.</summary>
    public static bool HasProject(string folder)
        => folder.Length > 0 && File.Exists(Path.Combine(folder, ProjectFolder.ManifestName));

    /// <summary>Last answer from <see cref="DescribeProject"/>, and what it was computed from.</summary>
    private static (string Folder, long Stamp, long Size, bool Ready, string Message) _projectCache;

    /// <summary>
    /// Looks at a folder and says whether it can be imported into, and why not when it cannot.
    /// </summary>
    public static (bool Ready, string Message) DescribeProject(string folder)
    {
        if (folder.Length == 0) return (false, "no project folder set");

        string manifestPath = Path.Combine(folder, ProjectFolder.ManifestName);
        if (!File.Exists(manifestPath)) return (false, "no project extracted here yet");

        long stamp, size;
        try
        {
            var info = new FileInfo(manifestPath);
            stamp = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
        }
        catch (IOException)
        {
            return (false, "manifest.json could not be read");
        }

        if (_projectCache.Folder == folder && _projectCache.Stamp == stamp && _projectCache.Size == size)
            return (_projectCache.Ready, _projectCache.Message);

        var answer = Describe(folder, manifestPath);
        _projectCache = (folder, stamp, size, answer.Ready, answer.Message);
        return answer;
    }

    private static (bool Ready, string Message) Describe(string folder, string manifestPath)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(SharedFile.ReadAllText(manifestPath));
            if (manifest is null || manifest.Blobs.Count == 0) return (false, "manifest.json is empty");

            int checks = Math.Min(24, manifest.Blobs.Count);
            int step = Math.Max(1, manifest.Blobs.Count / checks);
            int missing = 0;

            for (int i = 0; i < manifest.Blobs.Count; i += step)
            {
                string path = Path.Combine(folder,
                    manifest.Blobs[i].File.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) missing++;
            }

            if (missing > 0)
                return (false, $"the project here looks incomplete -- extract it again");

            return (true, $"{manifest.Blobs.Count:N0} assets extracted");
        }
        catch (Exception)
        {
            return (false, "manifest.json could not be read");
        }
    }

    /// <summary>
    /// Raised after an import writes an asset into the project folder, so the editor can reread it
    /// rather than waiting to observe its own filesystem writes through the folder watcher.
    /// </summary>
    public static event Action? ProjectAssetWritten;

    /// <summary>
    /// Moves a blob to the name and folder today's classifier would give it, when the project was
    /// extracted before the tool could recognise it.
    /// </summary>
    private static void Rehome(RomSession session, string project, int assetId, byte[] bytes)
    {
        string manifestPath = Path.Combine(project, ProjectFolder.ManifestName);
        if (!File.Exists(manifestPath)) return;

        ProjectManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ProjectManifest>(SharedFile.ReadAllText(manifestPath));
        }
        catch (Exception) { return; }                 // the write itself will report a bad manifest

        var blob = manifest?.Blobs.FirstOrDefault(b => b.Ids.Contains(assetId));
        if (manifest is null || blob is null || blob.Category != "other") return;

        var classifier = new ProjectFolder.Classifier(session.Rom, session.Assets);
        if (classifier.PlaceOf(session.Assets, blob.Ids, bytes) is not { } place) return;
        if (place.Category == "other" || place.Path == blob.File) return;

        string from = Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar));
        string to = Path.Combine(project, place.Path.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);

            blob.File = place.Path;
            blob.Category = place.Category;

            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { /* leave it where it was; the import still succeeds */ }
    }

    // ---- inventory icons ------------------------------------------------------------------------

    /// <summary>
    /// The palette the icons are drawn with, as the session sees it -- so if the inventory screen's
    /// palette has itself been replaced in the project, the icons follow it the way the game would.
    /// </summary>
    public static byte[] IconPalette(RomSession session)
    {
        if (!session.TryGetAsset(session.Rom.Layout.IconPaletteAsset, out var data) ||
            !MenuImage.TryParse(data, out var screen) ||
            screen!.Palettes.Count <= InventoryIcons.PaletteIndex)
            throw new InvalidDataException(
                $"Menu screen {session.Rom.Layout.IconPaletteAsset}, whose palette the icons use, could not be read.");

        return screen.PaletteToRgba(InventoryIcons.PaletteIndex);
    }

    /// <summary>One icon as RGBA, or null when its asset cannot be read.</summary>
    public static byte[]? IconRgba(RomSession session, InventoryIcons.Icon icon, byte[] palette)
        => session.TryGetAsset(icon.AssetId, out var data) && InventoryIcons.Fits(icon, data)
            ? InventoryIcons.ToRgba(data, icon, palette)
            : null;

    private static string IconFileName(InventoryIcons.Icon icon)
        => icon.InBundle ? $"icon_extra{icon.Number - InventoryIcons.Count:D2}.png" : $"icon{icon.ItemId:D3}.png";

    public static string ExportIconPng(RomSession session, InventoryIcons.Icon icon, string folder)
    {
        var rgba = IconRgba(session, icon, IconPalette(session))
                   ?? throw new InvalidDataException($"Asset {icon.AssetId} could not be read.");

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, IconFileName(icon));
        File.WriteAllBytes(path, ImageCodec.RgbaToPng(rgba, InventoryIcons.Width, InventoryIcons.Height));
        return path;
    }

    public static string ExportIcons(RomSession session, string folder)
    {
        var palette = IconPalette(session);
        Directory.CreateDirectory(folder);

        int written = 0;
        foreach (var icon in InventoryIcons.For(session.Rom.Layout))
        {
            var rgba = IconRgba(session, icon, palette);
            if (rgba is null) continue;

            File.WriteAllBytes(Path.Combine(folder, IconFileName(icon)),
                               ImageCodec.RgbaToPng(rgba, InventoryIcons.Width, InventoryIcons.Height));
            written++;
        }

        return $"wrote {written} icons to {folder}";
    }

    /// <summary>
    /// Replaces one icon from a 40x30 PNG, matching its colours into the inventory palette.
    /// </summary>
    public static string ImportIcon(RomSession session, string project, InventoryIcons.Icon icon, string pngPath)
    {
        using var loaded = Image.Load<Rgba32>(pngPath);

        if (loaded.Width != InventoryIcons.Width || loaded.Height != InventoryIcons.Height)
            throw new InvalidDataException(
                $"The image is {loaded.Width}x{loaded.Height}, but an icon must be exactly " +
                $"{InventoryIcons.Width}x{InventoryIcons.Height}.");

        if (!session.TryGetAsset(icon.AssetId, out var asset) || !InventoryIcons.Fits(icon, asset))
            throw new InvalidDataException($"Asset {icon.AssetId} could not be read.");

        var rgba = new byte[InventoryIcons.Size * 4];
        loaded.CopyPixelDataTo(rgba);

        var palette = IconPalette(session);
        var original = asset.AsSpan(icon.Offset, InventoryIcons.Size).ToArray();
        var pixels = InventoryIcons.Match(rgba, original, palette);

        // How faithful the match was, so a picture drawn in colours the palette lacks is not a
        // surprise only discovered in the game.
        int approximated = 0;
        for (int i = 0; i < InventoryIcons.Size; i++)
        {
            int c = pixels[i] * 4;
            if (palette[c] != rgba[i * 4] || palette[c + 1] != rgba[i * 4 + 1] || palette[c + 2] != rgba[i * 4 + 2])
                approximated++;
        }

        if (pixels.AsSpan().SequenceEqual(original))
            return $"{icon}: identical to what is there -- nothing written";

        string file = WriteProjectAsset(project, icon.AssetId, InventoryIcons.Replace(asset, icon, pixels));

        return $"{icon}: colours matched into palette {InventoryIcons.PaletteIndex} of menu screen " +
               $"{session.Rom.Layout.IconPaletteAsset}" +
               (approximated == 0
                   ? " -- every pixel exact"
                   : $" -- {approximated} of {InventoryIcons.Size} pixels took the nearest colour") +
               $"\nwrote {file} -- now Build ROM on the Project tab";
    }

    // ---- Japan's document pages -------------------------------------------------------------

    /// <summary>A document page as it currently reads (project edit applied), or null.</summary>
    public static JapaneseDocument? DocumentPage(RomSession session, int assetId)
        => session.TryGetAsset(assetId, out var data) && JapaneseDocument.TryDecode(data, out var page) ? page : null;

    /// <summary>RGBA for a page: the four levels as greys on black, which is how the game draws them.</summary>
    public static byte[] DocumentRgba(JapaneseDocument page)
    {
        var grey = page.ToGrey();
        var rgba = new byte[grey.Length * 4];
        for (int i = 0; i < grey.Length; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = grey[i];
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    public static string ExportDocumentPng(RomSession session, int assetId, string folder)
    {
        var page = DocumentPage(session, assetId)
                   ?? throw new InvalidDataException($"Asset {assetId} is not a document page.");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"page{assetId}.png");
        File.WriteAllBytes(path, ImageCodec.RgbaToPng(DocumentRgba(page), page.Width, page.Height));
        return path;
    }

    public static string ExportDocuments(RomSession session, IEnumerable<int> assetIds, string folder)
    {
        int written = 0;
        foreach (int id in assetIds)
            if (DocumentPage(session, id) is not null) { ExportDocumentPng(session, id, folder); written++; }
        return $"wrote {written:N0} pages to {folder}";
    }

    /// <summary>
    /// Replaces a page from a PNG of the same size. The page has four levels, so every pixel's
    /// brightness is rounded to the nearest of black, dark grey, light grey and white.
    /// </summary>
    public static string ImportDocumentPng(RomSession session, string project, int assetId, string pngPath)
    {
        var original = DocumentPage(session, assetId)
                       ?? throw new InvalidDataException($"Asset {assetId} is not a document page.");

        using var loaded = Image.Load<Rgba32>(pngPath);
        if (loaded.Width != original.Width || loaded.Height != original.Height)
            throw new InvalidDataException(
                $"The image is {loaded.Width}x{loaded.Height}, but this page is {original.Width}x{original.Height}.");

        var grey = new byte[loaded.Width * loaded.Height];
        loaded.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    // Rec. 601 luma, with transparency read as the black page behind it.
                    int luma = (p.R * 299 + p.G * 587 + p.B * 114) / 1000;
                    grey[y * row.Length + x] = (byte)(luma * p.A / 255);
                }
            }
        });

        var page = JapaneseDocument.FromGrey(original.Width, original.Height, grey);
        if (page.Pixels.AsSpan().SequenceEqual(original.Pixels))
            return $"page {assetId}: identical to what is there -- nothing written";

        var encoded = page.Encode();
        string file = WriteProjectAsset(project, assetId, encoded);
        return $"page {assetId}: {encoded.Length:N0} bytes coded\nwrote {file} -- now Build ROM on the Project tab";
    }

    /// <summary>An asset's bytes as the project folder holds them now, or null if it has none.</summary>
    private static byte[]? ReadProjectAsset(string project, int assetId)
    {
        try
        {
            string manifestPath = Path.Combine(project, ProjectFolder.ManifestName);
            if (!File.Exists(manifestPath)) return null;

            var manifest = JsonSerializer.Deserialize<ProjectManifest>(SharedFile.ReadAllText(manifestPath));
            var blob = manifest?.Blobs.FirstOrDefault(b => b.Ids.Contains(assetId));
            if (blob is null) return null;

            string path = Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? SharedFile.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string WriteProjectAsset(string project, int assetId, byte[] bytes)
    {
        string manifestPath = Path.Combine(project, ProjectFolder.ManifestName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException(
                "No extracted project. Use Extract on the Project tab first, then import into it.");

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(SharedFile.ReadAllText(manifestPath))
                       ?? throw new InvalidDataException("Could not read manifest.json.");

        var blob = manifest.Blobs.FirstOrDefault(b => b.Ids.Contains(assetId))
                   ?? throw new InvalidDataException($"Asset {assetId} is not in the project.");

        File.WriteAllBytes(Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar)), bytes);
        ProjectAssetWritten?.Invoke();
        return blob.File;
    }
}
