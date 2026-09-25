using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Re2.Core.Assets;
using Re2.Core.Codecs;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Core.Import;
using Re2.Core.Patch;
using Re2.Core.Project;
using Re2.Core.Rom;

namespace Re2.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "info" => CmdInfo(args),
                "map" => CmdMap(args),
                "dump-code" => CmdDumpCode(args),
                "bg" => CmdBackgrounds(args),
                "bgdiff" => CmdBgDiff(args),
                "files" => CmdFiles(args),
                "overlays" => CmdOverlays(args),
                "assets" => CmdAssets(args),
                "models" => CmdModels(args),
                "textures" => CmdTextures(args),
                "meshes" => CmdMeshes(args),
                "characters" => CmdCharacters(args),
                "items" => CmdItems(args),
                "room-props" => CmdRoomProps(args),
                "prop-rooms" => CmdPropRooms(args),
                "pair-sharing" => CmdPairSharing(args),
                "voice-export" => CmdVoiceExport(args),
                "voice-sweep" => CmdVoiceSweep(args),
                "mort-probe" => CmdMortProbe(args),
                "patch-create" => CmdPatchCreate(args),
                "patch-apply" => CmdPatchApply(args),
                "overlay-fit" => CmdOverlayFit(args),
                "sounds" => CmdSounds(args),
                "sound-import" => CmdSoundImport(args),
                "text" => CmdText(args),
                "text-import" => CmdTextImport(args),
                "mesh-import" => CmdMeshImport(args),
                "rig" => CmdRig(args),
                "mesh-info" => CmdMeshInfo(args),
                "mesh-survey" => CmdMeshSurvey(args),
                "mesh-header" => CmdMeshHeader(args),
                "character-sizes" => CmdCharacterSizes(args),
                "asset-diff" => CmdAssetDiff(args),
                "model-usage" => CmdModelUsage(args),
                "find-ids" => CmdFindIds(args),
                "overlay-dump" => CmdOverlayDump(args),
                "asset-what" => CmdAssetWhat(args),
                "overlay-save" => CmdOverlaySave(args),
                "model-texture-counts" => CmdModelTextureCounts(args),
                "mesh-validate" => CmdMeshValidate(args),
                "part-table" => CmdPartTable(args),
                "dir-diff" => CmdDirDiff(args),
                "texture-import" => CmdTextureImport(args),
                "anim-import" => CmdAnimImport(args),
                "extract" => CmdExtract(args),
                "build" => CmdBuild(args),
                "help" or "--help" or "-h" => Usage(),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            re2 - Resident Evil 2 (N64) romhacking suite

            Usage:
              re2 info <rom>                      Header, CIC and checksum status
              re2 map <rom>                       Known ROM region layout
              re2 dump-code <rom> --out <dir>     Extract the raw-deflate code overlay chain
              re2 overlays <rom> [--dump --out d]  Read the game overlay table (load addresses)
              re2 assets <rom> [--extract <dir>]  Read the game asset directory (8091 files)
              re2 models <rom> [--dump <dir>]     List character models / dump their sections
              re2 textures <rom> [--export <dir>] List / export the 1054 textures as PNG
              re2 meshes <rom> [--export <dir>]   List / export character meshes as .glb
              re2 characters <rom> [--export d]   Characters: mesh + textures + animation; --export writes animated .glb
              re2 sounds <rom> [--full] [--export <dir>] [--dump <dir>]
                                                  List the 1192 samples; --export writes decoded WAVs,
                                                  --dump writes the raw encoded blobs
              re2 sound-import <rom> --project <dir> --id N --wav f.wav   Replace a sample in a project
              re2 text <rom> [--full] [--export t.json]    List or export the readable text
              re2 text-import <rom> --project <dir> --json t.json   Write edited text back
              re2 mesh-import <rom> --project <dir> --asset N --glb model.glb
                                                  Replace a model with geometry from a glTF/GLB
              re2 texture-import <rom> --project <dir> --asset N --png image.png
                                                  Replace a texture (dimensions and palette size are fixed)
              re2 anim-import <rom> --project <dir> --character N --glb rigged.glb
                                                  Rewrite a character's poses from a glTF's rotation tracks
              re2 rig <rom> --character N         Inspect a character skeleton and its part mapping
              re2 mesh-info <rom> --asset N [--compare other.z64]
                                                  Break one model down part by part, and say how it
                                                  compares with the same asset in another ROM
              re2 extract <rom> --out <dir>       Extract every asset to an editable project folder
              re2 build <rom> --project <dir> --out new.z64   Rebuild a ROM from a project folder
              re2 bg list <rom> [--full]          Index the prerendered JPEG backgrounds
              re2 bg dump <rom> --out <dir>       Write every background out as .jpg
              re2 bg export <rom> --out <dir>     Decode backgrounds to PNG
              re2 bg replace <rom> --index N --image f.png --out new.z64
            """);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static string RequireArg(string[] args, int index, string what)
        => index < args.Length ? args[index] : throw new ArgumentException($"Missing <{what}>.");

    private static string? Option(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static RomFile LoadRom(string[] args, int index = 1)
    {
        var rom = RomFile.Load(RequireArg(args, index, "rom"));
        if (!Re2RomMap.IsExpectedRom(rom))
            Console.Error.WriteLine(
                $"warning: expected {Re2RomMap.ExpectedGameCode}, NREP or NB5J of at least " +
                $"{Re2RomMap.ExpectedLength / (1024 * 1024)} MB, got {rom.GameCode} at " +
                $"{rom.Length / (1024 * 1024)} MB. Offsets may not apply.");
        else if (Re2RomMap.IsExpanded(rom))
            Console.Error.WriteLine(
                $"note: this ROM is expanded to {rom.Length / (1024 * 1024)} MB, past the retail 64 MB.");
        return rom;
    }

    // ---- commands ---------------------------------------------------------

    private static int CmdInfo(string[] args)
    {
        var rom = LoadRom(args);
        Console.WriteLine($"file           {rom.SourcePath}");
        Console.WriteLine($"size           {rom.Length:N0} bytes ({rom.Length / (1024.0 * 1024.0):0.##} MB)");
        Console.WriteLine($"byte order     {rom.SourceByteOrder}");
        Console.WriteLine($"internal name  {rom.InternalName}");
        Console.WriteLine($"game code      {rom.GameCode}  (region '{rom.RegionCode}')");
        Console.WriteLine($"version        {Re2Version.Detect(rom)}  (header byte 0x{rom.Version:X2})");
        Console.WriteLine($"entry point    0x{rom.EntryPoint:X8}");
        Console.WriteLine($"cic            {rom.Cic}");
        Console.WriteLine($"crc1/crc2      0x{rom.Crc1:X8} / 0x{rom.Crc2:X8}");
        Console.WriteLine($"crc valid      {(rom.VerifyCrc() ? "yes" : "NO")}");
        return 0;
    }

    private static int CmdMap(string[] args)
    {
        var rom = LoadRom(args);
        Console.WriteLine($"{"start",-10} {"end",-10} {"size",12}  {"kind",-20} name");
        foreach (var r in Re2RomMap.Regions)
            Console.WriteLine($"0x{r.Start:X7}  0x{r.End:X7}  {r.Length,12:N0}  {r.Kind,-20} {r.Name}  {r.Notes}");

        long covered = Re2RomMap.Regions.Sum(r => (long)r.Length);
        Console.WriteLine();
        Console.WriteLine($"covered {covered:N0} / {rom.Length:N0} bytes");

        // These boundaries were measured on Rev 1.
        var version = Re2Version.Detect(rom);
        if (version.Release != Re2Release.UsaRev1)
            Console.WriteLine($"note: this is {version}; the boundaries above were measured on USA (Rev 1) " +
                              "and are approximate here. Use 'assets' for this ROM's own directory.");
        return 0;
    }

    /// <summary>
    /// Reads the game's overlay table and, with --dump, writes each decompressible overlay plus a
    /// bases.json that the Ghidra import script consumes.
    /// </summary>
    private static int CmdOverlays(string[] args)
    {
        var rom = LoadRom(args, 1);
        var entries = OverlayTable.Read(rom.Data);
        if (entries.Count == 0) return Fail("No overlay table in this ROM.");

        int table = OverlayTable.Locate(rom.Data);
        Console.WriteLine($"overlay table at ROM 0x{table:X} (RAM 0x{table - 0xB80 + 0x80000000:X8}), {entries.Count} entries");
        Console.WriteLine();
        Console.WriteLine($"{"idx",4} {"romOff",9} {"cSize",9} {"load",12} {"end",12} {"dSize",9}  status");

        var usable = new List<OverlayEntry>();
        foreach (var e in entries)
        {
            string status;
            if (e.IsEmpty) status = "empty";
            else if (OverlayTable.TryDecompress(rom.Data, e, out _)) { status = "deflate ok"; usable.Add(e); }
            else status = "not deflate (second bank)";

            Console.WriteLine($"{e.Index,4} 0x{e.RomOffset:X7} {e.CompressedSize,9:N0} 0x{e.LoadAddress:X8} 0x{e.EndAddress:X8} {e.DecompressedSize,9:N0}  {status}");
        }

        Console.WriteLine();
        Console.WriteLine($"{usable.Count} of {entries.Count} entries decompress as deflate");
        foreach (var g in usable.GroupBy(e => e.LoadAddress).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  0x{g.Key:X8}  {g.Count(),3} overlay(s) share this slot");

        if (!args.Contains("--dump")) return 0;

        string outDir = Option(args, "--out") ?? "overlays";
        Directory.CreateDirectory(outDir);

        var bases = new SortedDictionary<string, string>();
        foreach (var e in usable)
        {
            OverlayTable.TryDecompress(rom.Data, e, out var data);
            string name = $"ovl_{e.Index:D2}";
            File.WriteAllBytes(Path.Combine(outDir, name + ".bin"), data);
            bases[name] = $"0x{e.LoadAddress:X8}";
        }

        File.WriteAllText(Path.Combine(outDir, "bases.json"),
            JsonSerializer.Serialize(bases, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine($"wrote {usable.Count} overlays + bases.json to {Path.GetFullPath(outDir)}");
        return 0;
    }

    /// <summary>
    /// Reads the game's own asset directory and optionally extracts every decodable file.
    /// </summary>
    private static int CmdAssets(string[] args)
    {
        var rom = LoadRom(args, 1);
        var dir = AssetDirectory.Read(rom);

        Console.WriteLine($"asset base   ROM 0x{dir.AssetBaseRomOffset:X7}");
        Console.WriteLine($"file count   {dir.DeclaredFileCount:N0} ({dir.Entries.Count:N0} present, {dir.NullSlotCount:N0} null slots)");
        Console.WriteLine($"build stamp  {dir.BuildStamp}");
        Console.WriteLine();

        long decodable = 0;
        foreach (var g in dir.ByKind().OrderByDescending(g => g.Count()))
        {
            long stored = g.Sum(e => (long)e.StoredSize);
            long real = g.Sum(e => (long)e.DecompressedSize);
            string note = g.First().IsDecodable ? "" : "  (codec not decoded yet)";
            Console.WriteLine($"  {g.Key,-9} {g.Count(),6:N0} files  {stored,12:N0} stored -> {real,12:N0} bytes{note}");
            if (g.First().IsDecodable) decodable += g.Count();
        }

        Console.WriteLine();
        Console.WriteLine($"{decodable:N0} of {dir.Entries.Count:N0} files decode ({(decodable == dir.Entries.Count ? "every codec is implemented" : "some codecs are still unknown")})");

        string? outDir = Option(args, "--extract");
        if (outDir is null) return 0;

        Directory.CreateDirectory(outDir);
        int written = 0, failed = 0;
        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) { if (entry.IsDecodable) failed++; continue; }
            File.WriteAllBytes(Path.Combine(outDir, $"{entry.Index:D5}_{entry.Kind}.bin"), data);
            written++;
        }

        Console.WriteLine($"extracted {written:N0} files to {Path.GetFullPath(outDir)}" + (failed > 0 ? $" ({failed} failed)" : ""));
        return 0;
    }

    /// <summary>Finds every asset with the model layout and reports its structure.</summary>
    private static int CmdModels(string[] args)
    {
        var rom = LoadRom(args, 1);
        var dir = AssetDirectory.Read(rom);

        var models = new List<(AssetEntry Entry, ModelFile Model)>();
        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (ModelFile.TryParse(data, out var model)) models.Add((entry, model));
        }

        Console.WriteLine($"{models.Count:N0} model assets");
        if (models.Count > 0)
            Console.WriteLine($"asset id range {models[0].Entry.Index}..{models[^1].Entry.Index}");
        Console.WriteLine();

        if (args.Contains("--full"))
        {
            Console.WriteLine($"{"id",6} {"bytes",8} {"parts",6} {"blocks",6} {"clips",6}   section sizes");
            foreach (var (entry, model) in models)
                Console.WriteLine($"{entry.Index,6} {model.Data.Length,8:N0} {model.PartCount,6} {model.GeometryBlockOffsets.Count,6} {model.Clips.Count,6}   " +
                                  string.Join(" ", model.Sections.Select(s => $"{s.Size,7:N0}")));
        }

        foreach (var g in models.GroupBy(m => m.Model.BlocksPerPart).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Count(),4} models with {g.Key} geometry block(s) per part");
        Console.WriteLine($"parts: min {models.Min(m => m.Model.PartCount)}, max {models.Max(m => m.Model.PartCount)}");
        Console.WriteLine($"clips: min {models.Min(m => m.Model.Clips.Count)}, max {models.Max(m => m.Model.Clips.Count)}");

        string? outDir = Option(args, "--dump");
        if (outDir is null) return 0;

        Directory.CreateDirectory(outDir);
        foreach (var (entry, model) in models)
            for (int i = 0; i < model.Sections.Count; i++)
                File.WriteAllBytes(Path.Combine(outDir, $"{entry.Index:D5}_sec{i}.bin"), model.SectionData(i).ToArray());

        Console.WriteLine($"dumped {models.Count * ModelFile.SectionCount:N0} section files to {Path.GetFullPath(outDir)}");
        return 0;
    }

    /// <summary>Finds every texture asset and optionally decodes them all to PNG.</summary>
    private static int CmdTextures(string[] args)
    {
        var rom = LoadRom(args, 1);
        var dir = AssetDirectory.Read(rom);

        var textures = new List<(AssetEntry Entry, TextureFile Texture)>();
        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (TextureFile.TryParse(data, out var texture)) textures.Add((entry, texture));
        }

        Console.WriteLine($"{textures.Count:N0} textures, asset ids {textures[0].Entry.Index}..{textures[^1].Entry.Index}");
        Console.WriteLine();
        foreach (var g in textures.GroupBy(t => (t.Texture.Format, t.Texture.PaletteCount)).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  format {g.Key.Format}  palette {g.Key.PaletteCount,3}  {g.Count(),5:N0} textures");

        Console.WriteLine();
        foreach (var g in textures.GroupBy(t => $"{t.Texture.Width}x{t.Texture.Height}").OrderByDescending(g => g.Count()).Take(8))
            Console.WriteLine($"  {g.Key,-12} {g.Count(),5:N0}");

        string? outDir = Option(args, "--export");
        if (outDir is null) return 0;

        Directory.CreateDirectory(outDir);
        foreach (var (entry, texture) in textures)
        {
            var png = ImageCodec.RgbaToPng(texture.ToRgba(), texture.Width, texture.Height);
            File.WriteAllBytes(Path.Combine(outDir, $"tex{entry.Index:D5}_{texture.Width}x{texture.Height}.png"), png);
        }

        Console.WriteLine();
        Console.WriteLine($"exported {textures.Count:N0} PNGs to {Path.GetFullPath(outDir)}");
        return 0;
    }

    /// <summary>Finds every F3DEX2 character mesh and optionally exports them as glTF binary.</summary>
    private static int CmdMeshes(string[] args)
    {
        var rom = LoadRom(args, 1);
        var dir = AssetDirectory.Read(rom);

        var meshes = new List<(AssetEntry Entry, MeshFile Mesh)>();
        foreach (var entry in dir.Entries)
        {
            if (!dir.TryGetData(rom, entry, out var data)) continue;
            if (MeshFile.TryParse(data, out var mesh)) meshes.Add((entry, mesh));
        }

        Console.WriteLine($"{meshes.Count:N0} meshes, asset ids {meshes[0].Entry.Index}..{meshes[^1].Entry.Index}");
        Console.WriteLine($"total {meshes.Sum(m => (long)m.Mesh.TotalVertices):N0} vertices, {meshes.Sum(m => (long)m.Mesh.TotalTriangles):N0} triangles");
        Console.WriteLine();

        if (args.Contains("--full"))
            foreach (var (entry, mesh) in meshes)
                Console.WriteLine($"  #{entry.Index,-5} parts {mesh.PartCount,3}  verts {mesh.TotalVertices,6:N0}  tris {mesh.TotalTriangles,6:N0}");

        string? outDir = Option(args, "--export");
        if (outDir is null) return 0;

        Directory.CreateDirectory(outDir);
        foreach (var (entry, mesh) in meshes)
            GltfExporter.Save(mesh, Path.Combine(outDir, $"mesh{entry.Index:D5}.glb"), $"mesh{entry.Index}");

        Console.WriteLine($"exported {meshes.Count:N0} .glb files to {Path.GetFullPath(outDir)}");
        return 0;
    }

    /// <summary>Resolves each character to its mesh, texture set and animation assets.</summary>
    private static int CmdCharacters(string[] args)
    {
        var rom = LoadRom(args, 1);
        var overlay = ModelTextureTable.LoadMainOverlay(rom);
        var dir = AssetDirectory.Read(rom);

        // Cache texture counts once.
        var meshTextureCounts = new Dictionary<int, int>();
        foreach (var e in dir.Entries)
        {
            if (!dir.TryGetData(rom, e, out var meshData)) continue;
            if (MeshFile.TryParse(meshData, out var parsed)) meshTextureCounts[e.Index] = parsed.TextureCount;
        }

        Console.WriteLine($"{"tbl",3} {"idx",4} {"mesh",6} {"pair",6} {"tex",4} {"meshTex",7}  animations");

        int matched = 0, total = 0;
        for (int t = 0; t < ModelTextureTable.EntityTableAddresses.Length; t++)
        {
            foreach (var c in ModelTextureTable.ReadCharacters(overlay, t))
            {
                total++;
                int meshTextures = meshTextureCounts.TryGetValue(c.MeshAssetId, out int mt) ? mt : -1;
                if (meshTextures == c.Textures.Count) matched++;

                if (args.Contains("--full") || t == 0)
                    Console.WriteLine($"{t,3} {c.Index,4} {c.MeshAssetId,6} {c.PairIndex,6} {c.Textures.Count,4} {meshTextures,7}  " +
                                      string.Join(",", c.AnimationAssetIds));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"mesh texture count agrees with the pair record for {matched:N0} / {total:N0} characters");

        string? outDir = Option(args, "--export");
        if (outDir is null) return 0;

        Directory.CreateDirectory(outDir);

        var byId = dir.Entries.ToDictionary(e => e.Index);
        var seen = new HashSet<int>();
        int written = 0, skipped = 0, assembled = 0, animated = 0;

        for (int t = 0; t < ModelTextureTable.EntityTableAddresses.Length; t++)
        {
            foreach (var c in ModelTextureTable.ReadCharacters(overlay, t))
            {
                if (!seen.Add(c.MeshAssetId)) continue;

                if (!byId.TryGetValue(c.MeshAssetId, out var meshEntry)
                    || !dir.TryGetData(rom, meshEntry, out var meshData)
                    || !MeshFile.TryParse(meshData, out var mesh))
                {
                    skipped++;
                    continue;
                }

                var textures = new List<ExportTexture>();
                foreach (int texId in c.Textures.TextureIds)
                {
                    if (byId.TryGetValue(texId, out var texEntry)
                        && dir.TryGetData(rom, texEntry, out var texData)
                        && TextureFile.TryParse(texData, out var texture))
                    {
                        textures.Add(new ExportTexture(
                            ImageCodec.RgbaToPng(texture.ToRgba(), texture.Width, texture.Height),
                            texture.Width, texture.Height));
                    }
                    else
                    {
                        // Keep indices aligned even when a texture will not decode.
                        textures.Add(new ExportTexture(Array.Empty<byte>(), 1, 1));
                    }
                }

                // Slot [1] of the character's asset list is the clip table, slot [2] the pose bank.
                PoseBank? bank = null;
                AnimationSet? clips = null;

                if (c.AnimationAssetIds.Count > 1)
                {
                    try { bank = PoseBank.Assemble(rom, dir, c.AnimationAssetIds[1]); }
                    catch (Exception) { bank = null; }
                }
                if (bank is not null)
                {
                    try { clips = AnimationSet.Decode(rom, dir, c.AnimationAssetIds[0]); }
                    catch (Exception) { clips = null; }
                }

                bool usable = textures.TrueForAll(x => x.Png.Length > 0);
                string file = Path.Combine(outDir, $"char{c.MeshAssetId:D5}.glb");

                if (bank is not null)
                {
                    AnimatedGltfExporter.Save(mesh, bank, clips, usable ? textures : null, file, $"char{c.MeshAssetId}");
                    assembled++;
                    if (clips is not null) animated += clips.Clips.Count;
                }
                else
                {
                    GltfExporter.Save(mesh, usable ? textures : null, file, $"char{c.MeshAssetId}");
                }

                written += 1;
            }
        }

        Console.WriteLine($"exported {written:N0} textured .glb files to {Path.GetFullPath(outDir)}" +
                          (skipped > 0 ? $" ({skipped} characters had no parseable mesh)" : ""));
        Console.WriteLine($"{assembled:N0} assembled onto their skeleton, carrying {animated:N0} animation clips");
        return 0;
    }

    /// <summary>Reads the sample directory. The codec is not decoded yet, so dumps are raw.</summary>
    private static int CmdSounds(string[] args)
    {
        var rom = LoadRom(args, 1);
        var sound = SoundDirectory.Read(rom, AssetDirectory.Read(rom));

        Console.WriteLine($"{sound.Samples.Count:N0} samples, {sound.SampleData.Length:N0} bytes of sample data");
        Console.WriteLine();

        foreach (var g in sound.Samples.GroupBy(x => x.SampleRate).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,6} Hz  {g.Count(),5:N0} samples  {g.Sum(x => (long)x.StoredSize),12:N0} bytes");

        int looping = sound.Samples.Count(x => x.IsLooping);
        var bits = sound.Samples.Where(x => x.DeclaredLength > 0).Select(x => x.BitsPerSample).OrderBy(x => x).ToList();

        Console.WriteLine();
        Console.WriteLine($"looping samples      {looping:N0}");
        Console.WriteLine($"total playback time  {sound.Samples.Sum(x => x.Seconds) / 60:0.0} minutes");
        Console.WriteLine($"stored bits/sample   median {bits[bits.Count / 2]:0.00} " +
                          "(5 by construction: 40 bytes per 64 samples, plus the codebook)");

        if (args.Contains("--full"))
        {
            Console.WriteLine();
            Console.WriteLine($"{"id",5} {"offset",10} {"stored",9} {"rate",6} {"length",9} {"secs",6}  loop");
            foreach (var x in sound.Samples)
                Console.WriteLine($"{x.Index,5} 0x{x.Offset:X8} {x.StoredSize,9:N0} {x.SampleRate,6} {x.DeclaredLength,9:N0} {x.Seconds,6:0.00}  " +
                                  (x.IsLooping ? $"{x.LoopStart}..{x.LoopEnd}" : "-"));
        }

        // --export writes decoded WAVs; --dump writes the raw encoded bytes for format work.
        string? wavDir = Option(args, "--export");
        if (wavDir is not null)
        {
            Directory.CreateDirectory(wavDir);
            int written = 0;
            long pcmBytes = 0;
            var only = Option(args, "--id");

            foreach (var x in sound.Samples)
            {
                if (only is not null && x.Index != int.Parse(only)) continue;
                var pcm = sound.Decode(x);
                if (pcm.Length == 0) continue;
                var wav = WavCodec.Write(pcm, x.SampleRate);
                File.WriteAllBytes(Path.Combine(wavDir, $"snd{x.Index:D4}_{x.SampleRate}hz.wav"), wav);
                written++;
                pcmBytes += wav.Length;
            }

            Console.WriteLine();
            Console.WriteLine($"exported {written:N0} WAVs ({pcmBytes / 1024.0 / 1024.0:0.0} MB) to {Path.GetFullPath(wavDir)}");
        }

        string? outDir = Option(args, "--dump");
        if (outDir is null) return 0;

        Directory.CreateDirectory(outDir);
        foreach (var x in sound.Samples)
        {
            var data = sound.GetEncoded(x);
            if (data.Length == 0) continue;
            File.WriteAllBytes(Path.Combine(outDir, $"snd{x.Index:D4}_{x.SampleRate}hz.bin"), data.ToArray());
        }

        Console.WriteLine();
        Console.WriteLine($"dumped {sound.Samples.Count:N0} encoded samples to {Path.GetFullPath(outDir)}");
        return 0;
    }

    /// <summary>Extracts every asset to a project folder that "build" can turn back into a ROM.</summary>
    private static int CmdExtract(string[] args)
    {
        var rom = LoadRom(args, 1);
        string outDir = Option(args, "--out") ?? throw new ArgumentException("Missing --out.");

        int last = -1;
        var manifest = ProjectFolder.Extract(rom, outDir, (done, total) =>
        {
            int percent = done * 100 / total;
            if (percent == last) return;
            last = percent;
            if (percent % 10 == 0) Console.WriteLine($"  {percent,3}%  {done:N0} / {total:N0} blobs");
        });

        Console.WriteLine();
        Console.WriteLine($"extracted {manifest.Blobs.Count:N0} blobs covering " +
                          $"{manifest.Blobs.Sum(b => b.Ids.Count):N0} asset ids to {Path.GetFullPath(outDir)}");

        foreach (var g in manifest.Blobs.GroupBy(b => b.Kind).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-9} {g.Count(),6:N0} blobs");

        int shared = manifest.Blobs.Count(b => b.Ids.Count > 1);
        Console.WriteLine($"{shared:N0} blobs are shared by more than one asset id");
        Console.WriteLine("Edit the files under assets/ and run 'build' to produce a new ROM.");
        return 0;
    }

    /// <summary>Rebuilds a ROM from a project folder, relaying out the whole asset region.</summary>
    private static int CmdBuild(string[] args)
    {
        var rom = LoadRom(args, 1);
        string project = Option(args, "--project") ?? throw new ArgumentException("Missing --project.");
        string outPath = Option(args, "--out") ?? throw new ArgumentException("Missing --out.");

        // Builds are deliberately capped at the retail cart size.
        var output = RomFile.Load(rom.SourcePath!);
        var force = Option(args, "--force-rebuild");
        var forced = force is null
            ? null
            : force.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? AssetDirectory.Read(rom).Entries.Select(e => e.Index).ToHashSet()
                : force.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(
            SharedFile.ReadAllText(Path.Combine(project, ProjectFolder.ManifestName)));
        if (manifest is not null && ProjectFolder.ReleaseMismatch(manifest, rom) is { } mismatch)
            Console.WriteLine(mismatch);

        var result = ProjectFolder.Build(rom, project, output, forced);

        Console.WriteLine($"built from {Path.GetFileName(rom.SourcePath)} -- " +
                          $"Resident Evil 2 {Re2Version.Detect(rom)}");
        Console.WriteLine($"rebuilt {result.BlobsRebuilt:N0} edited blobs");
        Console.WriteLine($"{result.AssetsMoved:N0} assets had to move");
        if (result.AssetsMoved > 50)
            Console.WriteLine("\nNOTE: the region was compacted, so most assets are at new addresses.\n");

        Console.WriteLine($"asset region ends at 0x{result.BytesUsed:X7}, {result.BytesFree:N0} bytes free");
        Console.WriteLine(result.ByteIdentical
            ? "asset region is byte-identical to the source ROM"
            : "asset region differs from the source ROM (expected when something was edited)");

        // The item names sit in overlay 1 rather than the asset region, so they are reported apart
        // from it -- and a failure to apply them must not pass unmentioned just because the region
        // built cleanly.
        if (result.OverlayTextError.Length > 0)
            Console.WriteLine("overlay text NOT applied: " + result.OverlayTextError);
        else if (result.ItemNamesApplied || result.ItemTextApplied)
            Console.WriteLine(
                (result.ItemNamesApplied && result.ItemTextApplied ? "item names and examine text"
                 : result.ItemNamesApplied ? "item names"
                 : "item examine text") + " applied to overlay 1");

        output.Save(outPath);
        Console.WriteLine($"wrote {outPath} (checksum fixed)");
        return 0;
    }

    /// <summary>
    /// Diagnostic: compares the directory-driven background index against the old range scan, so a
    /// discrepancy in the count can be attributed rather than guessed at.
    /// </summary>
    private static int CmdBgDiff(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);
        var viaDirectory = BackgroundIndex.BuildFromDirectory(rom, directory);
        var viaScan = BackgroundIndex.Build(rom, Re2RomMap.BackgroundsStart, Re2RomMap.BackgroundsEnd);

        var scanned = viaScan.Backgrounds.Select(b => b.Offset).ToHashSet();
        var dirOffsets = viaDirectory.Backgrounds.Select(b => b.Offset).ToHashSet();

        Console.WriteLine($"directory {viaDirectory.Count:N0}   range scan {viaScan.Count:N0}");
        Console.WriteLine($"only in directory {viaDirectory.Backgrounds.Count(b => !scanned.Contains(b.Offset)):N0}   " +
                          $"only in scan {viaScan.Backgrounds.Count(b => !dirOffsets.Contains(b.Offset)):N0}");

        var byOffset = directory.Entries.GroupBy(e => e.RomOffset).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var b in viaDirectory.Backgrounds.Where(b => !scanned.Contains(b.Offset)))
        {
            byOffset.TryGetValue(b.Offset, out var es);
            Console.WriteLine($"  0x{b.Offset:X7}  {b.Length,8:N0} B  {b.Width}x{b.Height}  " +
                              $"stored {es?[0].StoredSize ?? 0,8:N0}  ids {(es is null ? "?" : string.Join(",", es.Select(e => e.Index)))}");
        }

        foreach (var b in viaScan.Backgrounds.Where(b => !dirOffsets.Contains(b.Offset)))
            Console.WriteLine($"  scan-only 0x{b.Offset:X7}  {b.Length,8:N0} B  {b.Width}x{b.Height}");

        return 0;
    }

    /// <summary>Replaces samples in an extracted project.</summary>
    private static int CmdSoundImport(string[] args)
    {
        var rom = LoadRom(args, 1);
        string project = Option(args, "--project") ?? throw new ArgumentException("Missing --project.");

        // --id N --wav file.wav may be repeated, in order.
        var replacements = new List<SoundBankBuilder.Replacement>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--id") continue;
            int id = int.Parse(args[i + 1]);
            string? wav = null;
            for (int j = i + 2; j < args.Length - 1; j++)
                if (args[j] == "--wav") { wav = args[j + 1]; break; }
            if (wav is null) throw new ArgumentException($"--id {id} has no matching --wav.");

            var (pcm, rate) = WavCodec.Read(File.ReadAllBytes(wav));
            replacements.Add(new SoundBankBuilder.Replacement(id, pcm, rate));
            Console.WriteLine($"sample {id} <- {Path.GetFileName(wav)}  {pcm.Length:N0} samples @ {rate} Hz " +
                              $"({pcm.Length / (double)rate:0.00}s)");
        }

        if (replacements.Count == 0) throw new ArgumentException("No --id/--wav pairs given.");

        var bank = SoundDirectory.Read(rom, AssetDirectory.Read(rom));
        var (table, data) = SoundBankBuilder.Rebuild(bank, replacements, Console.WriteLine);

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(
            File.ReadAllText(Path.Combine(project, ProjectFolder.ManifestName)))
            ?? throw new InvalidDataException("Could not read manifest.json.");

        void WriteAsset(int id, byte[] bytes)
        {
            var blob = manifest.Blobs.FirstOrDefault(b => b.Ids.Contains(id))
                       ?? throw new InvalidDataException($"Asset {id} is not in the project.");
            string path = Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  wrote {blob.File}  {bytes.Length:N0} bytes (was {blob.DeclaredSize:N0})");
        }

        WriteAsset(rom.Layout.SampleDirectoryAsset, table);
        WriteAsset(rom.Layout.SampleDataAsset, data);

        Console.WriteLine();
        Console.WriteLine($"replaced {replacements.Count} sample(s); run 'build' to produce the ROM.");
        return 0;
    }

    /// <summary>Lists or exports the game text. Entries are found by content, not a fixed id range.</summary>
    private static int CmdText(string[] args)
    {
        var rom = LoadRom(args, 1);
        var entries = TextTable.Read(rom, AssetDirectory.Read(rom));

        long characters = entries.Sum(e => (long)e.Text.Length);
        Console.WriteLine($"{entries.Count:N0} text assets, {characters:N0} characters");
        if (entries.Count > 0)
            Console.WriteLine($"asset ids {entries[0].AssetId}..{entries[^1].AssetId}   " +
                              string.Join("   ", entries.GroupBy(e => e.Kind)
                                                        .Select(g => $"{g.Key} {g.Count()}")));

        if (args.Contains("--full"))
        {
            Console.WriteLine();
            foreach (var e in entries)
            {
                Console.WriteLine($"--- {e.AssetId} ({e.Kind}, {e.Text.Length} chars, {e.Lines} lines)");
                Console.WriteLine(e.Text.Replace("\r\n", Environment.NewLine));
            }
        }

        string? json = Option(args, "--export");
        if (json is null) return 0;

        var payload = entries.Select(e => new TextJson(e.AssetId, e.Kind.ToString(), e.Text)).ToList();
        File.WriteAllText(json, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine($"exported {payload.Count:N0} strings to {Path.GetFullPath(json)}");
        Console.WriteLine("Edit the \"text\" fields and run text-import. Newlines may be \\n; they are stored as CRLF.");
        return 0;
    }

    private sealed record TextJson(int id, string kind, string text);

    /// <summary>Writes edited strings back into an extracted project, ready for build.</summary>
    private static int CmdTextImport(string[] args)
    {
        var rom = LoadRom(args, 1);
        string project = Option(args, "--project") ?? throw new ArgumentException("Missing --project.");
        string json = Option(args, "--json") ?? throw new ArgumentException("Missing --json.");

        var edited = JsonSerializer.Deserialize<List<TextJson>>(File.ReadAllText(json))
                     ?? throw new InvalidDataException("Could not read the text JSON.");

        var original = TextTable.Read(rom, AssetDirectory.Read(rom)).ToDictionary(e => e.AssetId);

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(
            File.ReadAllText(Path.Combine(project, ProjectFolder.ManifestName)))
            ?? throw new InvalidDataException("Could not read manifest.json.");

        int written = 0, unchanged = 0;
        foreach (var item in edited)
        {
            if (original.TryGetValue(item.id, out var before) &&
                before.Text.Replace("\r\n", "\n") == item.text.Replace("\r\n", "\n"))
            {
                unchanged++;
                continue;
            }

            var blob = manifest.Blobs.FirstOrDefault(b => b.Ids.Contains(item.id));
            if (blob is null)
            {
                Console.WriteLine($"  asset {item.id} is not in the project, skipped");
                continue;
            }

            var bytes = TextTable.Encode(item.text, rom.Layout.Latin1Documents);
            File.WriteAllBytes(Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar)), bytes);
            Console.WriteLine($"  {item.id}  {before?.Text.Length ?? 0} -> {bytes.Length} bytes");
            written++;
        }

        Console.WriteLine();
        Console.WriteLine($"{written:N0} strings edited, {unchanged:N0} unchanged; run 'build' to produce the ROM.");
        return 0;
    }

    /// <summary>Replaces a model's geometry from a glTF/GLB.</summary>
    private static int CmdMeshImport(string[] args)
    {
        var rom = LoadRom(args, 1);
        string project = Option(args, "--project") ?? throw new ArgumentException("Missing --project.");
        string glb = Option(args, "--glb") ?? throw new ArgumentException("Missing --glb.");
        int assetId = int.Parse(Option(args, "--asset") ?? throw new ArgumentException("Missing --asset."));

        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.FirstOrDefault(e => e.Index == assetId)
                    ?? throw new InvalidDataException($"Asset {assetId} is not in the ROM.");
        if (!directory.TryGetData(rom, entry, out var raw))
            throw new InvalidDataException($"Asset {assetId} would not decode.");
        if (!MeshFile.TryParse(raw, out var existing))
            throw new InvalidDataException($"Asset {assetId} is not a model.");

        Console.WriteLine($"replacing model {assetId}: {existing.PartCount} parts, " +
                          $"{existing.TotalVertices:N0} vertices, {existing.TotalTriangles:N0} triangles, " +
                          $"{existing.TextureCount} textures");

        // The exporter bakes each part's rest position into the glTF; undo it with the same values.
        IReadOnlyList<(int X, int Y, int Z)>? offsets = null;
        // The character tables are split across four banks; the mesh could be in any of them.
        ModelEntry? owner = null;
        for (int table = 0; table < 4 && owner is null; table++)
        {
            try { owner = ModelTextureTable.ReadCharacters(rom, table).FirstOrDefault(c => c.MeshAssetId == assetId); }
            catch (Exception) { /* a table that will not read simply has no match */ }
        }

        if (owner is not null && owner.AnimationAssetIds.Count > 1)
        {
            try
            {
                offsets = PoseBank.Assemble(rom, directory, owner.AnimationAssetIds[1]).RestWorldPositions();
                Console.WriteLine($"  using rest-pose offsets from character {owner.Index}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  could not read the rest pose ({ex.Message}); parts are taken as authored");
            }
        }
        else
        {
            Console.WriteLine("  no owning character found; parts are taken as authored about their own joint");
        }

        var (model, report) = GltfImporter.Load(glb, new GltfImporter.Options
        {
            PartOffsets = offsets,
            PartCount = existing.PartCount,
            TextureCount = existing.TextureCount
        });

        Console.WriteLine($"imported {report.Parts} parts ({report.PartsMatchedByName} matched by name), " +
                          $"{report.SubMeshes} sub-meshes, {report.Vertices:N0} vertices, " +
                          $"{report.Triangles:N0} triangles");

        foreach (var warning in report.Warnings) Console.WriteLine("  warning: " + warning);

        var bytes = MeshWriter.Write(model);
        var check = MeshFile.Parse(bytes);
        Console.WriteLine($"encoded {bytes.Length:N0} bytes; re-read gives {check.PartCount} parts, " +
                          $"{check.TotalVertices:N0} vertices, {check.TotalTriangles:N0} triangles");

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(
            File.ReadAllText(Path.Combine(project, ProjectFolder.ManifestName)))
            ?? throw new InvalidDataException("Could not read manifest.json.");

        var blob = manifest.Blobs.FirstOrDefault(b => b.Ids.Contains(assetId))
                   ?? throw new InvalidDataException($"Asset {assetId} is not in the project.");

        string target = Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(target, bytes);
        Console.WriteLine($"wrote {blob.File}  {bytes.Length:N0} bytes (was {blob.DeclaredSize:N0})");
        Console.WriteLine();
        Console.WriteLine("run 'build' to produce the ROM.");
        return 0;
    }

    /// <summary>Replaces one texture from a PNG, keeping its dimensions, format and palette size.</summary>
    private static int CmdTextureImport(string[] args)
    {
        var rom = LoadRom(args, 1);
        string project = Option(args, "--project") ?? throw new ArgumentException("Missing --project.");
        string png = Option(args, "--png") ?? throw new ArgumentException("Missing --png.");
        int assetId = int.Parse(Option(args, "--asset") ?? throw new ArgumentException("Missing --asset."));

        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.FirstOrDefault(e => e.Index == assetId)
                    ?? throw new InvalidDataException($"Asset {assetId} is not in the ROM.");
        if (!directory.TryGetData(rom, entry, out var raw) || !TextureFile.TryParse(raw, out var existing))
            throw new InvalidDataException($"Asset {assetId} is not a texture.");

        Console.WriteLine($"replacing texture {assetId}: {existing}");

        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(png);
        if (image.Width != existing.Width || image.Height != existing.Height)
            throw new InvalidDataException(
                $"'{Path.GetFileName(png)}' is {image.Width}x{image.Height}; the texture is " +
                $"{existing.Width}x{existing.Height} and its size cannot change.");

        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);

        var (bytes, report) = TextureWriter.Replace(existing, rgba, image.Width, image.Height);
        Console.WriteLine("  " + report);

        WriteProjectAsset(project, assetId, bytes);
        Console.WriteLine();
        Console.WriteLine("run 'build' to produce the ROM.");
        return 0;
    }

    /// <summary>Rewrites a character's poses from a rigged glTF.</summary>
    private static int CmdAnimImport(string[] args)
    {
        var rom = LoadRom(args, 1);
        string project = Option(args, "--project") ?? throw new ArgumentException("Missing --project.");
        string glb = Option(args, "--glb") ?? throw new ArgumentException("Missing --glb.");
        int characterIndex = int.Parse(Option(args, "--character") ?? throw new ArgumentException("Missing --character."));

        var directory = AssetDirectory.Read(rom);

        ModelEntry? character = null;
        for (int table = 0; table < 4 && character is null; table++)
        {
            try { character = ModelTextureTable.ReadCharacters(rom, table).FirstOrDefault(c => c.Index == characterIndex); }
            catch (Exception) { /* a table that will not read simply has no match */ }
        }

        if (character is null || character.AnimationAssetIds.Count < 2)
            throw new InvalidDataException($"Character {characterIndex} has no animation assets.");

        int indexAssetId = character.AnimationAssetIds[1];
        var bank = PoseBank.Assemble(rom, directory, indexAssetId);
        var clips = AnimationSet.Decode(rom, directory, character.AnimationAssetIds[0]);

        Console.WriteLine($"character {characterIndex}: {bank.PartCount} joints, {bank.PoseCount:N0} poses, " +
                          $"{clips.Clips.Count} clips");

        var report = GltfAnimationImporter.Load(glb, bank, clips);
        Console.WriteLine($"matched {report.ClipsMatched} clips, sampled {report.FramesSampled:N0} frames, " +
                          $"rewrote {report.PosesWritten:N0} poses");
        foreach (var warning in report.Warnings) Console.WriteLine("  warning: " + warning);

        // Cut the edited bank back into the assets it was assembled from.
        var chunks = PoseBankWriter.Layout(rom, directory, indexAssetId);
        var split = PoseBankWriter.SplitToAssets(bank.Data, chunks, out var splitWarnings);
        foreach (var warning in splitWarnings) Console.WriteLine("  warning: " + warning);

        int changed = 0;
        foreach (var (assetId, bytes) in split)
        {
            var entry = directory.Entries.FirstOrDefault(e => e.Index == assetId);
            if (entry is not null && directory.TryGetData(rom, entry, out var before) && before.AsSpan().SequenceEqual(bytes))
                continue;

            WriteProjectAsset(project, assetId, bytes);
            changed++;
        }

        Console.WriteLine();
        Console.WriteLine($"{changed} of {split.Count} pose assets changed; run 'build' to produce the ROM.");
        return 0;
    }

    /// <summary>Drops replacement bytes into an extracted project, by asset id.</summary>
    private static void WriteProjectAsset(string project, int assetId, byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(
            File.ReadAllText(Path.Combine(project, ProjectFolder.ManifestName)))
            ?? throw new InvalidDataException("Could not read manifest.json.");

        var blob = manifest.Blobs.FirstOrDefault(b => b.Ids.Contains(assetId))
                   ?? throw new InvalidDataException($"Asset {assetId} is not in the project.");

        File.WriteAllBytes(Path.Combine(project, blob.File.Replace('/', Path.DirectorySeparatorChar)), bytes);
        Console.WriteLine($"  wrote {blob.File}  {bytes.Length:N0} bytes (was {blob.DeclaredSize:N0})");
    }

    /// <summary>Breaks one model down part by part.</summary>
    private static int CmdMeshInfo(string[] args)
    {
        var rom = LoadRom(args, 1);
        int assetId = int.Parse(Option(args, "--asset") ?? throw new ArgumentException("Missing --asset."));

        var mesh = ReadMesh(rom, assetId, out int storedSize, out int decodedSize);

        Console.WriteLine($"asset {assetId}: {storedSize:N0} bytes stored, {decodedSize:N0} decoded");
        Console.WriteLine($"  {mesh.PartCount} parts, {mesh.TextureCount} texture slots, " +
                          $"{mesh.TotalVertices:N0} vertices, {mesh.TotalTriangles:N0} triangles");
        Console.WriteLine();

        Console.WriteLine("  part  subs   verts    tris  largest sub  texture slots");
        for (int p = 0; p < mesh.PartCount; p++)
        {
            var part = mesh.Parts[p];
            int verts = part.SubMeshes.Sum(sm => sm.Vertices.Count);
            int tris = part.SubMeshes.Sum(sm => sm.Triangles.Count);
            int largest = part.SubMeshes.Count == 0 ? 0 : part.SubMeshes.Max(sm => sm.Vertices.Count);
            string slots = string.Join(",", part.SubMeshes.Select(sm => sm.TextureIndex).Distinct().OrderBy(t => t));

            Console.WriteLine($"  {p,4}  {part.SubMeshes.Count,4}  {verts,6:N0}  {tris,6:N0}  " +
                              $"{largest,11:N0}  {slots}");
        }

        string? other = Option(args, "--compare");
        if (other is not null)
        {
            var otherRom = RomFile.Load(other);
            var otherMesh = ReadMesh(otherRom, assetId, out int otherStored, out int otherDecoded);

            Console.WriteLine();
            Console.WriteLine($"compared with {Path.GetFileName(other)}:");
            Console.WriteLine($"  stored     {otherStored,8:N0} -> {storedSize,8:N0}");
            Console.WriteLine($"  decoded    {otherDecoded,8:N0} -> {decodedSize,8:N0}");
            Console.WriteLine($"  parts      {otherMesh.PartCount,8} -> {mesh.PartCount,8}");
            Console.WriteLine($"  textures   {otherMesh.TextureCount,8} -> {mesh.TextureCount,8}");
            Console.WriteLine($"  vertices   {otherMesh.TotalVertices,8:N0} -> {mesh.TotalVertices,8:N0}");
            Console.WriteLine($"  triangles  {otherMesh.TotalTriangles,8:N0} -> {mesh.TotalTriangles,8:N0}");
            Console.WriteLine($"  sub-meshes {otherMesh.Parts.Sum(x => x.SubMeshes.Count),8} -> " +
                              $"{mesh.Parts.Sum(x => x.SubMeshes.Count),8}");
        }

        return 0;
    }

    /// <summary>
    /// Surveys every model in the ROM for the shapes an importer might produce, so "is this legal?"
    /// can be answered from the game's own data instead of guessed at.
    /// </summary>
    private static int CmdMeshSurvey(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);

        int meshes = 0, withEmptyParts = 0, emptyParts = 0;
        int biggestSub = 0, biggestDecoded = 0, mostParts = 0;
        var emptyExamples = new List<string>();

        foreach (var entry in directory.Entries.OrderBy(e => e.Index))
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var mesh)) continue;

            meshes++;
            mostParts = Math.Max(mostParts, mesh.PartCount);
            biggestDecoded = Math.Max(biggestDecoded, data.Length);

            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                    biggestSub = Math.Max(biggestSub, sub.Vertices.Count);

            int empties = mesh.Parts.Count(p => p.SubMeshes.Count == 0);
            if (empties > 0)
            {
                withEmptyParts++;
                emptyParts += empties;
                if (emptyExamples.Count < 8)
                    emptyExamples.Add($"asset {entry.Index} ({empties} of {mesh.PartCount})");
            }
        }

        Console.WriteLine($"{meshes} models examined");
        Console.WriteLine($"  models with an empty part : {withEmptyParts}  ({emptyParts} such parts)");
        foreach (var example in emptyExamples) Console.WriteLine($"      {example}");
        Console.WriteLine($"  most parts on any model   : {mostParts}");
        Console.WriteLine($"  largest sub-mesh          : {biggestSub:N0} vertices");
        Console.WriteLine($"  largest decoded model     : {biggestDecoded:N0} bytes");

        return 0;
    }

    /// <summary>
    /// The size of every model reachable as a character, which is what the entity heap has to hold.
    /// </summary>
    private static int CmdCharacterSizes(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);
        var byId = directory.Entries.ToDictionary(e => e.Index);

        var seen = new Dictionary<int, int>();

        for (int table = 0; table < ModelTextureTable.EntityTableAddresses.Length; table++)
        {
            IReadOnlyList<ModelEntry> characters;
            try { characters = ModelTextureTable.ReadCharacters(rom, table); }
            catch (Exception) { continue; }

            foreach (var character in characters)
            {
                if (seen.ContainsKey(character.MeshAssetId)) continue;
                if (!byId.TryGetValue(character.MeshAssetId, out var entry)) continue;
                if (!directory.TryGetData(rom, entry, out var data)) continue;
                if (!MeshFile.TryParse(data, out _)) continue;

                seen[character.MeshAssetId] = data.Length;
            }
        }

        Console.WriteLine($"{seen.Count} distinct character models");
        foreach (var (assetId, size) in seen.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  mesh {assetId,5}: {size,8:N0} bytes decoded");

        return 0;
    }

    /// <summary>Every asset whose bytes differ between two ROMs, with both decoded sizes.</summary>
    private static int CmdAssetDiff(string[] args)
    {
        var a = LoadRom(args, 1);
        var b = LoadRom(args, 2);

        var da = AssetDirectory.Read(a);
        var db = AssetDirectory.Read(b);
        var bById = db.Entries.ToDictionary(e => e.Index);

        int differing = 0, resized = 0;

        foreach (var ea in da.Entries)
        {
            if (!bById.TryGetValue(ea.Index, out var eb)) { Console.WriteLine($"  {ea.Index,5}: missing in second ROM"); continue; }

            byte[]? ba = da.TryGetData(a, ea, out var x) ? x : null;
            byte[]? bb = db.TryGetData(b, eb, out var y) ? y : null;

            if (ba is null || bb is null)
            {
                if ((ba is null) != (bb is null))
                    Console.WriteLine($"  {ea.Index,5}: decodes in one ROM but not the other");
                continue;
            }

            if (ba.AsSpan().SequenceEqual(bb)) continue;

            differing++;
            string note = ba.Length == bb.Length ? "" : "   <-- SIZE CHANGED";
            if (ba.Length != bb.Length) resized++;

            int firstDiff = 0;
            while (firstDiff < Math.Min(ba.Length, bb.Length) && ba[firstDiff] == bb[firstDiff]) firstDiff++;

            Console.WriteLine($"  {ea.Index,5}: {ba.Length,8:N0} -> {bb.Length,8:N0} bytes, " +
                              $"first difference at 0x{firstDiff:X}{note}");
        }

        Console.WriteLine($"{differing} assets differ, {resized} of them changed size");
        return 0;
    }

    /// <summary>
    /// Walks a model's display lists the strict way the RSP does, reporting anything the microcode
    /// would choke on.
    /// </summary>
    private static int CmdMeshValidate(string[] args)
    {
        var rom = LoadRom(args, 1);
        int assetId = int.Parse(Option(args, "--asset") ?? throw new ArgumentException("Missing --asset."));

        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == assetId);
        if (!directory.TryGetData(rom, entry, out var data))
            throw new InvalidDataException($"Asset {assetId} would not decode.");

        if (!MeshFile.TryParse(data, out var model) || model is null)
            throw new InvalidDataException($"Asset {assetId} is not a model.");

        const int Slots = MeshFile.VertexBufferSlots;
        int problems = 0;

        void Bad(int part, int sub, string what)
        {
            if (problems++ < 40) Console.WriteLine($"  part {part,2} sub {sub,2}: {what}");
        }

        for (int p = 0; p < model.Parts.Count; p++)
        for (int m = 0; m < model.Parts[p].SubMeshes.Count; m++)
        {
            var sub = model.Parts[p].SubMeshes[m];

            // The vertex block itself has to sit inside the asset.
            long blockEnd = (long)sub.VertexOffset + (long)sub.VertexCount * 16;
            if (sub.VertexOffset < 0 || blockEnd > data.Length)
                Bad(p, m, $"vertex block {sub.VertexOffset}..{blockEnd} is outside the {data.Length}-byte asset");

            var loaded = new int[Slots];
            Array.Fill(loaded, -1);

            int at = sub.DisplayListOffset, commands = 0;
            bool ended = false;

            while (!ended)
            {
                if (at < 0 || at + 8 > data.Length) { Bad(p, m, $"display list runs off the end of the asset at 0x{at:X}"); break; }
                if (++commands > 200000)            { Bad(p, m, "display list never terminates"); break; }

                byte op = data[at];
                uint w0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
                uint w1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 4));
                at += 8;

                if (op == 0xDF) { ended = true; break; }

                if (op == 0x01)
                {
                    int count = (int)((w0 >> 12) & 0xFF);
                    int destination = (int)((w0 & 0xFFF) >> 1) - count;
                    int byteOffset = (int)(w1 & 0xFFFFFF);
                    int firstVertex = byteOffset / 16;

                    if (count <= 0)
                        Bad(p, m, $"G_VTX loads {count} vertices");
                    if (destination < 0 || destination + count > Slots)
                        Bad(p, m, $"G_VTX writes slots {destination}..{destination + count - 1}, outside the {Slots}-slot buffer");
                    if (byteOffset % 16 != 0)
                        Bad(p, m, $"G_VTX address 0x{byteOffset:X} is not vertex-aligned");
                    if (firstVertex < 0 || firstVertex + count > sub.VertexCount)
                        Bad(p, m, $"G_VTX reads vertices {firstVertex}..{firstVertex + count - 1} of {sub.VertexCount}");

                    for (int k = 0; k < count; k++)
                    {
                        int slot = destination + k;
                        if (slot >= 0 && slot < Slots) loaded[slot] = firstVertex + k;
                    }
                    continue;
                }

                if (op == 0x05 || op == 0x06)
                {
                    void Check(uint packed, string which)
                    {
                        int slot = (int)(packed & 0xFF) / 2;
                        if (slot >= Slots)       Bad(p, m, $"{which} indexes slot {slot}, past the {Slots}-slot buffer");
                        else if (loaded[slot] < 0) Bad(p, m, $"{which} indexes slot {slot}, which no G_VTX has loaded");
                    }

                    Check(w0 >> 16, "G_TRI"); Check(w0 >> 8, "G_TRI"); Check(w0, "G_TRI");
                    if (op == 0x06)
                    {
                        Check(w1 >> 16, "G_TRI2"); Check(w1 >> 8, "G_TRI2"); Check(w1, "G_TRI2");
                    }
                }
            }

            if (!ended && problems < 40) Bad(p, m, "display list has no G_ENDDL");
        }

        Console.WriteLine(problems == 0
            ? $"asset {assetId}: display lists are clean ({model.Parts.Count} parts, {model.TotalVertices:N0} vertices)"
            : $"asset {assetId}: {problems} problem(s)");
        return 0;
    }

    /// <summary>Dumps a model's raw 16-byte part records.</summary>
    private static int CmdPartTable(string[] args)
    {
        var rom = LoadRom(args, 1);
        int assetId = int.Parse(Option(args, "--asset") ?? throw new ArgumentException("Missing --asset."));

        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == assetId);
        if (!directory.TryGetData(rom, entry, out var data))
            throw new InvalidDataException($"Asset {assetId} would not decode.");

        static uint U32(byte[] d, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));

        int partCount = (int)U32(data, 4);
        int table = (int)U32(data, 8);

        Console.WriteLine($"asset {assetId}: {data.Length:N0} bytes, {partCount} parts, table at 0x{table:X}");
        Console.WriteLine("  idx    count    primary  secondary      extra   (flag = inside the asset?)");

        for (int i = 0; i < partCount; i++)
        {
            int at = table + i * 16;
            if (at + 16 > data.Length) { Console.WriteLine($"  {i,3}: record runs past the end of the asset"); break; }

            uint count = U32(data, at), primary = U32(data, at + 4),
                 secondary = U32(data, at + 8), extra = U32(data, at + 12);

            static string Mark(uint v, int length) => v == 0 ? "-" : (v < length ? "ok" : "OUT OF RANGE");

            Console.WriteLine($"  {i,3}: {count,8} 0x{primary:X8} 0x{secondary:X8} 0x{extra:X8}   " +
                              $"{Mark(primary, data.Length),-12} {Mark(secondary, data.Length),-12} {Mark(extra, data.Length)}");
        }

        return 0;
    }

    /// <summary>
    /// Compares two ROMs' asset directories by position rather than content: how many assets moved,
    /// changed encoding, or changed stored size.
    /// </summary>
    private static int CmdDirDiff(string[] args)
    {
        var a = LoadRom(args, 1);
        var b = LoadRom(args, 2);

        var da = AssetDirectory.Read(a);
        var db = AssetDirectory.Read(b);
        var bById = db.Entries.ToDictionary(e => e.Index);

        int moved = 0, resized = 0, recoded = 0, shown = 0;
        int firstMoved = int.MaxValue, lastMoved = -1;

        foreach (var ea in da.Entries)
        {
            if (!bById.TryGetValue(ea.Index, out var eb)) continue;

            bool m = ea.RomOffset != eb.RomOffset;
            bool r = ea.StoredSize != eb.StoredSize;
            bool k = ea.Kind != eb.Kind;

            if (m) { moved++; firstMoved = Math.Min(firstMoved, ea.Index); lastMoved = Math.Max(lastMoved, ea.Index); }
            if (r) resized++;
            if (k) recoded++;

            if ((r || k) && shown++ < 20)
                Console.WriteLine($"  {ea.Index,5}: 0x{ea.RomOffset:X7} {ea.Kind} {ea.StoredSize:N0}B  ->  " +
                                  $"0x{eb.RomOffset:X7} {eb.Kind} {eb.StoredSize:N0}B");
        }

        Console.WriteLine($"{moved:N0} assets moved, {resized:N0} changed stored size, {recoded:N0} changed encoding");
        if (moved > 0) Console.WriteLine($"moved range: asset {firstMoved} .. {lastMoved}");
        return 0;
    }

    /// <summary>
    /// Every model asset in the cart, saying which are reachable through the character tables and
    /// which are not. The unclaimed ones are what the editor cannot currently show.
    /// </summary>
    private static int CmdModelUsage(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);

        var characterMeshes = new HashSet<int>();
        for (int table = 0; table < ModelTextureTable.EntityTableAddresses.Length; table++)
        {
            try
            {
                foreach (var character in ModelTextureTable.ReadCharacters(rom, table))
                    characterMeshes.Add(character.MeshAssetId);
            }
            catch (Exception) { }
        }

        var models = new List<(int Id, int Size, int Parts, int Textures)>();
        foreach (var entry in directory.Entries)
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var mesh) || mesh is null) continue;
            models.Add((entry.Index, data.Length, mesh.Parts.Count, mesh.TextureCount));
        }

        Console.WriteLine($"{models.Count} models, {characterMeshes.Count} reachable as characters");
        Console.WriteLine();

        // Runs of consecutive ids, so the layout of the unclaimed ones is visible at a glance.
        var unclaimed = models.Where(m => !characterMeshes.Contains(m.Id)).ToList();
        Console.WriteLine($"{unclaimed.Count} unclaimed models:");

        int runStart = -1, previous = -2;
        var run = new List<(int Id, int Size, int Parts, int Textures)>();

        void Flush()
        {
            if (run.Count == 0) return;
            Console.WriteLine($"  ids {runStart}..{previous}  ({run.Count} models)  " +
                              $"parts {run.Min(m => m.Parts)}-{run.Max(m => m.Parts)}  " +
                              $"textures {run.Min(m => m.Textures)}-{run.Max(m => m.Textures)}  " +
                              $"size {run.Min(m => m.Size):N0}-{run.Max(m => m.Size):N0}");
            run.Clear();
        }

        foreach (var m in unclaimed)
        {
            if (m.Id != previous + 1) { Flush(); runStart = m.Id; }
            run.Add(m);
            previous = m.Id;
        }
        Flush();

        return 0;
    }

    /// <summary>
    /// Scans the main overlay for runs of u16 values inside a range, which is how a table of asset
    /// ids is found when the code that reads it has not been located yet.
    /// </summary>
    private static int CmdFindIds(string[] args)
    {
        var rom = LoadRom(args, 1);
        int low = int.Parse(Option(args, "--low") ?? "0");
        int high = int.Parse(Option(args, "--high") ?? "65535");
        int minimumRun = int.Parse(Option(args, "--run") ?? "4");
        bool holes = args.Contains("--holes");

        var overlay = ModelTextureTable.LoadMainOverlay(rom);

        Console.WriteLine($"overlay 1: {overlay.Data.Length:N0} bytes at 0x{overlay.BaseAddress:X8}");
        Console.WriteLine($"looking for runs of >= {minimumRun} u16 values in {low}..{high}");

        int runStart = -1, count = 0;

        void Flush(int endAt)
        {
            if (count >= minimumRun)
            {
                uint address = overlay.BaseAddress + (uint)runStart;
                Console.Write($"  0x{address:X8}  {count} values, stride 2:");

                for (int at = runStart; at < endAt && at < runStart + 2 * 24; at += 2)
                    Console.Write($" {System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(overlay.Data.AsSpan(at))}");

                Console.WriteLine(count > 24 ? " ..." : "");
            }
            count = 0;
        }

        for (int at = 0; at + 2 <= overlay.Data.Length; at += 2)
        {
            int value = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(overlay.Data.AsSpan(at));

            // 0xFFFF marks an unused slot, so it belongs to the run rather than ending it.
            bool empty = holes && value == 0xFFFF;

            if (empty || (value >= low && value <= high))
            {
                if (count == 0) runStart = at;
                count++;
            }
            else Flush(at);
        }

        Flush(overlay.Data.Length);
        return 0;
    }

    /// <summary>Dumps a stretch of the main overlay as u16 words, for reading a table by eye.</summary>
    private static int CmdOverlayDump(string[] args)
    {
        var rom = LoadRom(args, 1);
        uint address = Convert.ToUInt32(Option(args, "--at") ?? throw new ArgumentException("Missing --at."), 16);
        int words = int.Parse(Option(args, "--words") ?? "64");
        int perLine = int.Parse(Option(args, "--per-line") ?? "8");

        var overlay = ModelTextureTable.LoadMainOverlay(rom);

        for (int i = 0; i < words; i += perLine)
        {
            uint lineAt = address + (uint)i * 2;
            Console.Write($"  0x{lineAt:X8} ");

            for (int j = 0; j < perLine && i + j < words; j++)
            {
                uint wordAt = lineAt + (uint)j * 2;
                if (!overlay.Contains(wordAt, 2)) { Console.Write("   ----"); continue; }
                Console.Write($" {overlay.U16(wordAt),6}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>Says what kind of thing an asset is, by trying each parser the way the tabs do.</summary>
    private static int CmdAssetWhat(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);
        var byId = directory.Entries.ToDictionary(e => e.Index);

        foreach (string text in (Option(args, "--ids") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int id = int.Parse(text);

            if (!byId.TryGetValue(id, out var entry)) { Console.WriteLine($"  {id,5}: no such asset"); continue; }
            if (!directory.TryGetData(rom, entry, out var data)) { Console.WriteLine($"  {id,5}: does not decode"); continue; }

            string what = "unknown";
            if (MeshFile.TryParse(data, out var mesh) && mesh is not null)
                what = $"MODEL   {mesh.Parts.Count} parts, {mesh.TextureCount} textures, {mesh.TotalVertices} verts";
            else if (TextureFile.TryParse(data, out var texture) && texture is not null)
                what = $"TEXTURE {texture.Width}x{texture.Height}, format {texture.Format}, palette {texture.PaletteCount}";

            Console.WriteLine($"  {id,5}: {entry.Kind,-8} {data.Length,7:N0}B  {what}");
        }

        return 0;
    }

    /// <summary>
    /// Lists the item models and cross-checks each against its mesh: the pair record's texture count
    /// has to equal the count in the mesh's own header, which is what proves the two tables line up.
    /// </summary>
    private static int CmdItems(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);
        var items = ItemTable.Read(rom);

        Console.WriteLine($"{items.Count} item entries in {ItemTable.Count} slots");
        Console.WriteLine();
        Console.WriteLine("  slot   mesh  parts  verts  textures  pair  agrees");

        int agree = 0, checkedCount = 0;
        var distinct = new HashSet<int>();

        foreach (var item in items)
        {
            distinct.Add(item.MeshAssetId);

            var entry = directory.Entries.FirstOrDefault(e => e.Index == item.MeshAssetId);
            string shape = "     -      -";
            string verdict = "no mesh";

            if (entry is not null && directory.TryGetData(rom, entry, out var data)
                && MeshFile.TryParse(data, out var mesh) && mesh is not null)
            {
                shape = $"{mesh.Parts.Count,6} {mesh.TotalVertices,6}";
                checkedCount++;
                bool ok = mesh.TextureCount == item.Textures.Count;
                if (ok) agree++;
                verdict = ok ? "yes" : $"NO ({mesh.TextureCount} in header)";
            }

            Console.WriteLine($"  {item.Index,4} {item.MeshAssetId,6} {shape} {item.Textures.Count,9} " +
                              $"{item.PairIndex,5}  {verdict}");
        }

        Console.WriteLine();
        Console.WriteLine($"{distinct.Count} distinct meshes; {agree}/{checkedCount} agree with their mesh header");
        return 0;
    }

    /// <summary>Writes the decompressed main overlay to a file, for analysis outside this tool.</summary>
    private static int CmdOverlaySave(string[] args)
    {
        var rom = LoadRom(args, 1);
        string outPath = Option(args, "--out") ?? throw new ArgumentException("Missing --out.");

        var overlay = ModelTextureTable.LoadMainOverlay(rom);
        File.WriteAllBytes(outPath, overlay.Data);

        Console.WriteLine($"wrote {overlay.Data.Length:N0} bytes, loads at 0x{overlay.BaseAddress:X8}");
        return 0;
    }

    /// <summary>Prints each model asset's declared texture count, for correlating against a table.</summary>
    private static int CmdModelTextureCounts(string[] args)
    {
        var rom = LoadRom(args, 1);
        int low = int.Parse(Option(args, "--low") ?? "0");
        int high = int.Parse(Option(args, "--high") ?? "65535");

        var directory = AssetDirectory.Read(rom);

        foreach (var entry in directory.Entries.Where(e => e.Index >= low && e.Index <= high))
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (!MeshFile.TryParse(data, out var mesh) || mesh is null) continue;
            Console.WriteLine($"{entry.Index} {mesh.TextureCount} {mesh.Parts.Count} {mesh.TotalVertices}");
        }

        return 0;
    }

    /// <summary>
    /// Walks the per-room prop lists and reports how many scenery models they account for.
    /// </summary>
    private static int CmdRoomProps(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);
        var props = RoomPropTable.Read(rom);

        var models = new HashSet<int>();
        var multiple = 0;
        var byModel = new Dictionary<int, HashSet<int>>();

        foreach (var prop in props)
        {
            models.Add(prop.MeshAssetId);
            if (!byModel.TryGetValue(prop.MeshAssetId, out var set))
                byModel[prop.MeshAssetId] = set = new HashSet<int>();
            set.Add(prop.PairIndex);
        }

        foreach (var (_, set) in byModel) if (set.Count > 1) multiple++;

        Console.WriteLine($"{props.Count} prop entries naming {models.Count} distinct models");
        Console.WriteLine($"{multiple} models appear with more than one texture set");

        // What is in the cart but not reachable this way.
        var scenery = new List<int>();
        foreach (var entry in directory.Entries)
        {
            if (!directory.TryGetData(rom, entry, out var data)) continue;
            if (MeshFile.TryParse(data, out _)) scenery.Add(entry.Index);
        }

        var missing = scenery.Where(id => id >= 7875 && id <= 8083 && !models.Contains(id)).ToList();
        Console.WriteLine($"models 7875..8083 not named by any room: {missing.Count}");
        if (missing.Count > 0) Console.WriteLine("  " + string.Join(", ", missing.Take(30)));

        var outside = models.Where(id => id < 7875 || id > 8083).OrderBy(x => x).ToList();
        Console.WriteLine($"models named outside 7875..8083: {outside.Count}");
        if (outside.Count > 0) Console.WriteLine("  " + string.Join(", ", outside.Take(30)));

        return 0;
    }

    /// <summary>How far a texture-slot edit would reach.</summary>
    private static int CmdPairSharing(string[] args)
    {
        var rom = LoadRom(args, 1);
        var overlay = ModelTextureTable.LoadMainOverlay(rom);

        var users = new Dictionary<int, List<string>>();

        void Note(int pairIndex, string who)
        {
            if (!users.TryGetValue(pairIndex, out var list)) users[pairIndex] = list = new List<string>();
            list.Add(who);
        }

        for (int table = 0; table < ModelTextureTable.EntityTableAddresses.Length; table++)
            foreach (var character in ModelTextureTable.ReadCharacters(overlay, table))
                Note(character.PairIndex, $"character mesh {character.MeshAssetId}");

        foreach (var item in ItemTable.Read(overlay))
            Note(item.PairIndex, $"item slot {item.Index} mesh {item.MeshAssetId}");

        var models = new HashSet<int>();
        var directory = AssetDirectory.Read(rom);
        foreach (var entry in directory.Entries)
            if (directory.TryGetData(rom, entry, out var data) && MeshFile.TryParse(data, out _))
                models.Add(entry.Index);

        foreach (var prop in RoomPropTable.Read(overlay, models))
            Note(prop.PairIndex, $"prop mesh {prop.MeshAssetId}");

        // Distinct users, since one mesh can be reached by several scenario tables.
        var shared = users.Where(kv => kv.Value.Distinct().Count() > 1).ToList();

        Console.WriteLine($"{users.Count} pair records referenced");
        Console.WriteLine($"{shared.Count} of them are used by more than one model");
        Console.WriteLine();

        foreach (var (pairIndex, who) in shared.OrderByDescending(kv => kv.Value.Distinct().Count()).Take(10))
            Console.WriteLine($"  pair {pairIndex,5}: {who.Distinct().Count()} models -- " +
                              string.Join(", ", who.Distinct().Take(4)));

        return 0;
    }

    /// <summary>
    /// Whether an overlay, recompressed by our own encoder, still fits the slot the ROM gives it.
    /// </summary>
    private static int CmdOverlayFit(string[] args)
    {
        var rom = LoadRom(args, 1);
        var entries = OverlayTable.Read(rom.Data);

        Console.WriteLine(" idx     slot   recompressed   spare");

        foreach (var entry in entries.Where(e => !e.IsEmpty))
        {
            if (int.TryParse(Option(args, "--overlay"), out int only) && entry.Index != only) continue;
            if (!OverlayTable.TryDecompress(rom.Data, entry, out var data)) continue;

            var packed = Zlib.Compress(data);
            int spare = entry.CompressedSize - packed.Length;

            Console.WriteLine($" {entry.Index,3} {entry.CompressedSize,8:N0} {packed.Length,14:N0} {spare,8:N0}" +
                              (spare < 0 ? "   DOES NOT FIT" : ""));
        }

        return 0;
    }

    /// <summary>Decodes one voice clip and writes it as a WAV, for listening to the result.</summary>
    private static int CmdVoiceExport(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == rom.Layout.VoiceBankAsset);
        directory.TryGetData(rom, entry, out var bank);

        var clips = VoiceBank.Read(bank);
        int index = int.Parse(Option(args, "--clip") ?? "0");
        string outPath = Option(args, "--out") ?? $"voice{index:D4}.wav";

        var clip = clips.First(c => c.Index == index);
        var cpu = Re2.Core.Emulation.GameCode.Load(rom);

        var samples = MortCodec.Decode(cpu, bank.AsSpan(clip.Offset, clip.StoredSize), clip.BlockCount);

        Console.WriteLine($"clip {clip.Index}: {clip.BlockCount} blocks, {clip.SampleRate} Hz, " +
                          $"{clip.Seconds:0.00}s, {samples.Length:N0} samples");

        int peak = 0; long energy = 0;
        foreach (short s in samples) { peak = Math.Max(peak, Math.Abs((int)s)); energy += (long)s * s; }
        Console.WriteLine($"peak {peak}, rms {Math.Sqrt((double)energy / Math.Max(1, samples.Length)):0.0}");

        File.WriteAllBytes(outPath, WavCodec.Write(samples, clip.SampleRate));
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    /// <summary>Decodes every clip in the voice bank, reporting anything that fails.</summary>
    private static int CmdVoiceSweep(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == rom.Layout.VoiceBankAsset);
        directory.TryGetData(rom, entry, out var bank);

        var clips = VoiceBank.Read(bank);
        var cpu = Re2.Core.Emulation.GameCode.Load(rom);

        int decoded = 0, failed = 0, silent = 0;
        double seconds = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        foreach (var clip in clips)
        {
            try
            {
                var samples = MortCodec.Decode(cpu, bank.AsSpan(clip.Offset, clip.StoredSize), clip.BlockCount);

                int peak = 0;
                foreach (short s in samples) peak = Math.Max(peak, Math.Abs((int)s));
                if (peak == 0) silent++;

                decoded++;
                seconds += clip.Seconds;
            }
            catch (Exception ex)
            {
                if (failed++ < 5) Console.WriteLine($"  clip {clip.Index}: {ex.Message}");
            }
        }

        Console.WriteLine($"{decoded}/{clips.Count} clips decoded, {failed} failed, {silent} silent");
        Console.WriteLine($"{seconds / 60:0.0} minutes of audio in {clock.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }

    /// <summary>
    /// Probes the MORT decoder: feeds it chosen coefficients and reports what comes out.
    /// </summary>
    private static int CmdMortProbe(string[] args)
    {
        var rom = LoadRom(args, 1);
        var cpu = Re2.Core.Emulation.GameCode.Load(rom);

        const int blocks = 6;

        double[] DecodeWith(Action<MortCodec.Block> set)
        {
            var list = new List<MortCodec.Block>();
            for (int i = 0; i < blocks; i++)
            {
                var block = MortCodec.Block.Zero();
                if (i == 2) set(block);                 // perturb one block in the middle
                list.Add(block);
            }

            var clip = MortCodec.WriteBlocks(list, 16000);
            var pcm = MortCodec.Decode(cpu, clip, blocks);
            return pcm.Select(x => (double)x).ToArray();
        }

        var base_ = DecodeWith(_ => { });
        Console.WriteLine($"all-zero blocks: peak {base_.Max(Math.Abs):0}, rms {Math.Sqrt(base_.Sum(x => x * x) / base_.Length):0.0}");

        // Two single-coefficient perturbations, then both together.
        var a = DecodeWith(b => b.Groups[0].Coefficients[0] = 4);
        var c = DecodeWith(b => b.Groups[1].Coefficients[5] = 4);
        var both = DecodeWith(b => { b.Groups[0].Coefficients[0] = 4; b.Groups[1].Coefficients[5] = 4; });

        double[] Delta(double[] x) => x.Zip(base_, (p, q) => p - q).ToArray();

        var da = Delta(a); var dc = Delta(c); var dboth = Delta(both);
        var sum = da.Zip(dc, (p, q) => p + q).ToArray();

        double error = 0, scale = 0;
        for (int i = 0; i < sum.Length; i++)
        {
            error += Math.Abs(dboth[i] - sum[i]);
            scale += Math.Abs(dboth[i]);
        }

        Console.WriteLine($"single change A: energy {da.Sum(Math.Abs):N0}");
        Console.WriteLine($"single change B: energy {dc.Sum(Math.Abs):N0}");
        Console.WriteLine($"both together  : energy {dboth.Sum(Math.Abs):N0}, sum of singles {sum.Sum(Math.Abs):N0}");
        Console.WriteLine($"superposition error: {(scale > 0 ? error / scale * 100 : 0):0.00}% " +
                          (error <= scale * 0.02 ? "-> LINEAR" : "-> NOT linear"));

        // How far does one block's change reach? Transform codecs overlap into neighbours.
        for (int b = 0; b < blocks; b++)
        {
            double reach = 0;
            for (int i = 0; i < MortCodec.SamplesPerBlock; i++)
                reach += Math.Abs(da[b * MortCodec.SamplesPerBlock + i]);
            Console.WriteLine($"  block {b}: response energy {reach:N0}");
        }

        return 0;
    }

    /// <summary>Writes a patch describing the difference between an original ROM and a built one.</summary>
    private static int CmdPatchCreate(string[] args)
    {
        var original = File.ReadAllBytes(args[1]);
        var modified = File.ReadAllBytes(Option(args, "--modified") ?? throw new ArgumentException("Missing --modified."));
        string outPath = Option(args, "--out") ?? Path.ChangeExtension(args[1], ".bps");

        var patch = BpsPatch.Create(original, modified, "Created by RE2 Studio");
        File.WriteAllBytes(outPath, patch);

        int changed = 0;
        for (int i = 0; i < Math.Min(original.Length, modified.Length); i++)
            if (original[i] != modified[i]) changed++;

        Console.WriteLine($"{patch.Length:N0}-byte patch describing {changed:N0} changed bytes " +
                          $"of a {original.Length:N0}-byte ROM");
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    /// <summary>Applies a patch to an original ROM, which is how a patch is checked.</summary>
    private static int CmdPatchApply(string[] args)
    {
        var original = File.ReadAllBytes(args[1]);
        var patch = File.ReadAllBytes(Option(args, "--patch") ?? throw new ArgumentException("Missing --patch."));
        string outPath = Option(args, "--out") ?? "patched.z64";

        var result = BpsPatch.Apply(patch, original);
        File.WriteAllBytes(outPath, result.Target);

        if (result.Metadata.Length > 0) Console.WriteLine($"patch says: {result.Metadata}");
        Console.WriteLine($"wrote {outPath} ({result.Target.Length:N0} bytes)");
        return 0;
    }

    /// <summary>How many distinct rooms place each scenery model.</summary>
    private static int CmdPropRooms(string[] args)
    {
        var rom = LoadRom(args, 1);
        var directory = AssetDirectory.Read(rom);
        var overlay = ModelTextureTable.LoadMainOverlay(rom);

        var models = new HashSet<int>();
        foreach (var entry in directory.Entries)
            if (directory.TryGetData(rom, entry, out var data) && MeshFile.TryParse(data, out _))
                models.Add(entry.Index);

        // Every placement, not the de-duplicated set the editor uses.
        var rooms = new Dictionary<int, HashSet<(int Stage, int Room)>>();
        foreach (var prop in RoomPropTable.ReadAll(overlay, models))
        {
            if (!rooms.TryGetValue(prop.MeshAssetId, out var where))
                rooms[prop.MeshAssetId] = where = new HashSet<(int, int)>();
            where.Add((prop.Stage, prop.Room));
        }

        int low = int.Parse(Option(args, "--min") ?? "2");
        int high = int.Parse(Option(args, "--max") ?? "20");

        Console.WriteLine($"models placed in {low}..{high} distinct rooms:");
        foreach (var (mesh, where) in rooms.Where(r => r.Value.Count >= low && r.Value.Count <= high)
                                           .OrderBy(r => r.Value.Count))
            Console.WriteLine($"  mesh {mesh,5}: {where.Count,3} rooms  " +
                              string.Join(" ", where.OrderBy(w => w).Select(w => $"{w.Stage}-{w.Room}")));

        return 0;
    }

    /// <summary>Dumps a model's raw header words, for comparing against what the game expects.</summary>
    private static int CmdMeshHeader(string[] args)
    {
        var rom = LoadRom(args, 1);
        int assetId = int.Parse(Option(args, "--asset") ?? throw new ArgumentException("Missing --asset."));

        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.First(e => e.Index == assetId);
        if (!directory.TryGetData(rom, entry, out var data))
            throw new InvalidDataException($"Asset {assetId} would not decode.");

        Console.WriteLine($"asset {assetId}: {data.Length:N0} bytes");
        Console.WriteLine($"  @0  u16 version    {System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0)):N0}");
        Console.WriteLine($"  @2  u16 relocated  {System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2))}");

        for (int at = 4; at < 0x20; at += 4)
            Console.WriteLine($"  @{at,-2} u32          0x{System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)):X8}  " +
                              $"{System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)):N0}");

        return 0;
    }

    private static MeshFile ReadMesh(RomFile rom, int assetId, out int storedSize, out int decodedSize)
    {
        var directory = AssetDirectory.Read(rom);
        var entry = directory.Entries.FirstOrDefault(e => e.Index == assetId)
                    ?? throw new InvalidDataException($"Asset {assetId} is not in the ROM.");

        if (!directory.TryGetData(rom, entry, out var data))
            throw new InvalidDataException($"Asset {assetId} would not decode.");

        storedSize = entry.StoredSize;
        decodedSize = data.Length;
        return MeshFile.Parse(data);
    }

    /// <summary>Prints a character's skeleton next to its mesh's parts.</summary>
    private static int CmdRig(string[] args)
    {
        var rom = LoadRom(args, 1);
        int index = int.Parse(Option(args, "--character") ?? throw new ArgumentException("Missing --character."));

        var directory = AssetDirectory.Read(rom);

        ModelEntry? character = null;
        for (int table = 0; table < 4 && character is null; table++)
        {
            try { character = ModelTextureTable.ReadCharacters(rom, table).FirstOrDefault(c => c.Index == index); }
            catch (Exception) { /* a table that will not read simply has no match */ }
        }

        if (character is null) throw new InvalidDataException($"No character {index}.");

        // --bank overrides which asset the rig comes from, for checking a model against a rig other
        // than the one slot 1 names.
        int bankAsset = int.TryParse(Option(args, "--bank"), out int overrideBank)
            ? overrideBank
            : character.AnimationAssetIds[1];
        var bank = PoseBank.Assemble(rom, directory, bankAsset);
        MeshFile? mesh = null;
        var entry = directory.Entries.FirstOrDefault(e => e.Index == character.MeshAssetId);
        if (entry is not null && directory.TryGetData(rom, entry, out var raw)) MeshFile.TryParse(raw, out mesh);

        Console.WriteLine($"character {index}: mesh {character.MeshAssetId}, rig from asset {bankAsset}" +
                          $"   (animation assets: {string.Join(",", character.AnimationAssetIds)})");
        Console.WriteLine($"  rig joints  {bank.PartCount}   root {bank.RootIndex}   poses {bank.PoseCount}");
        Console.WriteLine($"  mesh parts  {(mesh is null ? "unreadable" : mesh.PartCount.ToString())}");

        // Which joints the hierarchy walk actually reaches from the root.
        var reached = new bool[bank.PartCount];
        var stack = new Stack<int>();
        stack.Push(bank.RootIndex);
        while (stack.Count > 0)
        {
            int j = stack.Pop();
            if (j < 0 || j >= bank.PartCount || reached[j]) continue;
            reached[j] = true;
            foreach (int child in bank.Joints[j].Children) stack.Push(child);
        }

        var orphans = Enumerable.Range(0, bank.PartCount).Where(j => !reached[j]).ToList();
        Console.WriteLine($"  reachable from root: {reached.Count(r => r)}/{bank.PartCount}" +
                          (orphans.Count == 0 ? "" : $"   ORPHANS: {string.Join(", ", orphans)}"));
        Console.WriteLine();

        // How far each joint actually turns across the whole bank.
        var maxTurn = new int[bank.PartCount];
        for (int q = 0; q < bank.PoseCount; q++)
        {
            var pose = bank.GetPose(q);
            for (int j = 0; j < bank.PartCount; j++)
            {
                var (ax, ay, az) = pose.Angles[j];
                int Deviation(int a) => Math.Min(a, PoseBank.AngleUnitsPerTurn - a);
                maxTurn[j] = Math.Max(maxTurn[j], Math.Max(Deviation(ax), Math.Max(Deviation(ay), Deviation(az))));
            }
        }

        Console.WriteLine();
        Console.WriteLine("raw hierarchy words (the game copies flags out of these):");
        for (int j = 0; j < bank.PartCount; j++)
        {
            uint word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                bank.Data.AsSpan(bank.HierarchyOffset + j * 4, 4));
            Console.WriteLine($"  joint {j,2}: 0x{word:X8}   childCount 0x{word & 0xFFFF:X4}   " +
                              $"childOffset 0x{word >> 16:X4}" +
                              (((word & 0x200) != 0) ? "   <== bit 0x200 set" : ""));
        }

        Console.WriteLine();
        Console.WriteLine($"{"joint",5} {"rest offset",22} {"parts",6} {"local vertex centre",24} {"extent",8} {"max |rotation|",15}  children");
        for (int j = 0; j < bank.PartCount; j++)
        {
            var joint = bank.Joints[j];
            string parts = "-", centre = "-", extent = "-";

            if (mesh is not null && j < mesh.PartCount)
            {
                var part = mesh.Parts[j];
                parts = part.SubMeshes.Count.ToString();

                var vertices = part.SubMeshes.SelectMany(m => m.Vertices).ToList();
                if (vertices.Count > 0)
                {
                    // Geometry authored about its own joint sits near the origin in part space; a
                    // part centred far away is authored somewhere else and will not follow the rig.
                    int minX = vertices.Min(v => (int)v.X), maxX = vertices.Max(v => (int)v.X);
                    int minY = vertices.Min(v => (int)v.Y), maxY = vertices.Max(v => (int)v.Y);
                    int minZ = vertices.Min(v => (int)v.Z), maxZ = vertices.Max(v => (int)v.Z);
                    centre = $"({(minX + maxX) / 2}, {(minY + maxY) / 2}, {(minZ + maxZ) / 2})";
                    extent = $"{Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ))}";
                }
            }

            string turn = $"{maxTurn[j] * 360.0 / PoseBank.AngleUnitsPerTurn:0.0} deg";
            Console.WriteLine($"{j,5} {$"({joint.X}, {joint.Y}, {joint.Z})",22} {parts,6} {centre,24} {extent,8} {turn,15}  " +
                              (joint.Children.Count == 0 ? "-" : string.Join(", ", joint.Children)) +
                              (reached[j] ? "" : "   <== never reached"));
        }

        // A joint that carries no geometry of its own still moves its children.
        var restWorld = bank.RestWorldPositions();
        var worstGap = new double[bank.PartCount];
        var worstPose = new int[bank.PartCount];

        for (int q = 0; q < bank.PoseCount; q++)
        {
            var world = WorldPositions(bank, bank.GetPose(q));
            for (int j = 0; j < bank.PartCount; j++)
            {
                double dx = world[j].X - restWorld[j].X;
                double dy = world[j].Y - restWorld[j].Y;
                double dz = world[j].Z - restWorld[j].Z;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d <= worstGap[j]) continue;
                worstGap[j] = d;
                worstPose[j] = q;
            }
        }

        // Per clip, so a model that only breaks in some animations can be told from one that is
        // wrong everywhere.
        try
        {
            int clipAsset = int.TryParse(Option(args, "--clips"), out int overrideClips)
                ? overrideClips
                : character.AnimationAssetIds[0];
            var clips = AnimationSet.Decode(rom, directory, clipAsset);
            Console.WriteLine();
            Console.WriteLine($"per clip, worst travel of a joint whose parent has no geometry " +
                              $"(from clip asset {clipAsset}):");
            foreach (var clip in clips.Clips)
            {
                double worst = 0;
                foreach (int poseIndex in clip.PoseIndices)
                {
                    if (poseIndex < 0 || poseIndex >= bank.PoseCount) continue;
                    var world = WorldPositions(bank, bank.GetPose(poseIndex));
                    var rest = bank.RestWorldPositions();
                    for (int j = 0; j < bank.PartCount; j++)
                    {
                        double dx = world[j].X - rest[j].X, dy = world[j].Y - rest[j].Y, dz = world[j].Z - rest[j].Z;
                        worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                    }
                }
                // How far the geometry-less joints themselves turn: if an animation leaves them
                // alone, a model that bakes their limbs into the torso still looks right.
                double stubTurn = 0;
                foreach (int poseIndex in clip.PoseIndices)
                {
                    if (poseIndex < 0 || poseIndex >= bank.PoseCount) continue;
                    var pose = bank.GetPose(poseIndex);
                    foreach (int j in new[] { 1, 2, 5, 9, 12 })
                    {
                        if (j >= bank.PartCount) continue;
                        var (ax, ay, az) = pose.Angles[j];
                        int Dev(int a) => Math.Min(a, PoseBank.AngleUnitsPerTurn - a);
                        stubTurn = Math.Max(stubTurn, Math.Max(Dev(ax), Math.Max(Dev(ay), Dev(az))));
                    }
                }
                Console.WriteLine($"  clip {clip.Index,2}: {clip.FrameCount,4} frames, worst travel {worst,8:N0} units, " +
                                  $"stub joints turn up to {stubTurn * 360.0 / PoseBank.AngleUnitsPerTurn,6:0.0} deg");
            }
        }
        catch (Exception ex) { Console.WriteLine("  (clips unavailable: " + ex.Message + ")"); }

        Console.WriteLine();
        Console.WriteLine("how far each joint origin travels from its rest position:");
        for (int j = 0; j < bank.PartCount; j++)
        {
            bool stub = mesh is not null && j < mesh.PartCount &&
                        mesh.Parts[j].SubMeshes.SelectMany(m => m.Vertices).Count() <= 8;
            Console.WriteLine($"  joint {j,2}: up to {worstGap[j],8:N0} units (pose {worstPose[j]})" +
                              (stub ? "   <== no geometry of its own" : ""));
        }

        if (mesh is not null && mesh.PartCount > bank.PartCount)
        {
            Console.WriteLine();
            Console.WriteLine($"mesh parts {bank.PartCount}..{mesh.PartCount - 1} have no joint:");
            for (int p = bank.PartCount; p < mesh.PartCount; p++)
            {
                var vertices = mesh.Parts[p].SubMeshes.SelectMany(m => m.Vertices).ToList();
                string where = "empty";
                if (vertices.Count > 0)
                {
                    int minX = vertices.Min(v => (int)v.X), maxX = vertices.Max(v => (int)v.X);
                    int minY = vertices.Min(v => (int)v.Y), maxY = vertices.Max(v => (int)v.Y);
                    int minZ = vertices.Min(v => (int)v.Z), maxZ = vertices.Max(v => (int)v.Z);
                    where = $"centre ({(minX + maxX) / 2}, {(minY + maxY) / 2}, {(minZ + maxZ) / 2}) " +
                            $"extent {Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ))}";
                }
                Console.WriteLine($"  part {p}: {mesh.Parts[p].SubMeshes.Count} sub-mesh(es), " +
                                  $"{vertices.Count} vertices, {where}");
            }
        }

        return 0;
    }

    /// <summary>Model-space origin of every joint under a given pose, walking the rig.</summary>
    private static System.Numerics.Vector3[] WorldPositions(PoseBank bank, Pose pose)
    {
        var result = new System.Numerics.Vector3[bank.PartCount];
        void Walk(int index, System.Numerics.Matrix4x4 parent)
        {
            if (index < 0 || index >= bank.PartCount) return;

            var joint = bank.Joints[index];
            var local = System.Numerics.Matrix4x4.CreateTranslation(joint.X, joint.Y, joint.Z);
            var (ax, ay, az) = pose.Angles[index];
            var rotation = System.Numerics.Matrix4x4.CreateRotationX(PoseBank.AngleToRadians(ax))
                         * System.Numerics.Matrix4x4.CreateRotationY(PoseBank.AngleToRadians(ay))
                         * System.Numerics.Matrix4x4.CreateRotationZ(PoseBank.AngleToRadians(az));
            var world = (rotation * local) * parent;
            result[index] = world.Translation;
            foreach (int child in joint.Children) Walk(child, world);
        }
        Walk(bank.RootIndex, System.Numerics.Matrix4x4.Identity);
        return result;
    }

    private static int CmdFiles(string[] args)
    {
        var rom = LoadRom(args, 1);

        int ParseOffset(string? text, int fallback)
            => text is null ? fallback
             : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                 ? int.Parse(text[2..], NumberStyles.HexNumber)
                 : int.Parse(text);

        int stop = ParseOffset(Option(args, "--stop"), Re2RomMap.LastUsedByte);

        int start;
        if (args.Contains("--rewind"))
        {
            int known = ParseOffset(Option(args, "--start"), Re2RomMap.BackgroundsStart);
            Console.WriteLine($"walking backwards from 0x{known:X7} to find the container start ...");
            start = AssetContainer.FindContainerStart(rom.Data, known);
            Console.WriteLine($"container starts at 0x{start:X7}");
        }
        else if (args.Contains("--find-anchor"))
        {
            int from = ParseOffset(Option(args, "--from"), Re2RomMap.DataTablesStart);
            int to = ParseOffset(Option(args, "--to"), from + 0x40000);
            Console.WriteLine($"searching 0x{from:X7}..0x{to:X7} for a file boundary ...");
            start = AssetContainer.FindFirstFile(rom.Data, from, to);
            if (start < 0) return Fail("No file boundary found in that range.");
            Console.WriteLine($"anchor found at 0x{start:X7}");
        }
        else
        {
            start = ParseOffset(Option(args, "--start"), Re2RomMap.BackgroundsStart);
        }

        var files = AssetContainer.Walk(rom.Data, start, stop);
        Console.WriteLine($"walked {files.Count:N0} files from 0x{start:X7}");
        if (files.Count > 0)
            Console.WriteLine($"chain ends at 0x{files[^1].NextOffset:X7} (last file 0x{files[^1].Offset:X7} +{files[^1].Size:N0})");

        Console.WriteLine();
        Console.WriteLine("tag histogram:");
        foreach (var g in files.GroupBy(f => f.Tag).OrderByDescending(g => g.Count()).Take(20))
            Console.WriteLine($"  0x{g.Key:X8}  {g.Count(),7:N0} files  {g.Sum(f => (long)f.Size),13:N0} bytes");

        if (args.Contains("--full"))
            foreach (var f in files) Console.WriteLine($"  {f}");

        Console.WriteLine();
        Console.WriteLine($"total payload {files.Sum(f => (long)f.Size):N0} bytes over {(files.Count > 0 ? files[^1].NextOffset - start : 0):N0} bytes of ROM");
        return 0;
    }

    private static int CmdBackgrounds(string[] args)
    {
        string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        var rom = LoadRom(args, 2);

        Console.WriteLine("indexing baseline JFIF backgrounds via the asset directory ...");
        var index = BackgroundIndex.Build(rom);

        switch (sub)
        {
            case "list":
                if (args.Contains("--full"))
                    foreach (var bg in index.Backgrounds)
                        Console.WriteLine($"  {bg}  slot {bg.SlotCapacity:N0}B");

                Console.WriteLine();
                Console.WriteLine($"found {index.Count:N0} backgrounds");
                foreach (var (dims, count) in index.DimensionHistogram())
                    Console.WriteLine($"  {dims,-10} {count,6:N0}");

                int adjacent = index.CountAdjacentPairsMatchingPacking();
                Console.WriteLine();
                Console.WriteLine($"container packing rule holds for {adjacent:N0} / {index.Count - 1:N0} adjacent pairs");
                Console.WriteLine($"total {index.Backgrounds.Sum(b => (long)b.Length):N0} bytes");
                return 0;

            case "dump":
            {
                string outDir = Option(args, "--out") ?? "backgrounds";
                Directory.CreateDirectory(outDir);
                foreach (var bg in index.Backgrounds)
                    File.WriteAllBytes(Path.Combine(outDir, $"bg{bg.Index:D4}.jpg"), index.GetJpegBytes(rom, bg.Index).ToArray());
                Console.WriteLine($"wrote {index.Count:N0} images to {Path.GetFullPath(outDir)}");
                return 0;
            }

            case "export":
            {
                string outDir = Option(args, "--out") ?? "backgrounds";
                Directory.CreateDirectory(outDir);

                string? only = Option(args, "--index");
                var targets = only is null
                    ? index.Backgrounds
                    : new[] { index[int.Parse(only)] };

                foreach (var bg in targets)
                {
                    var png = BackgroundCodec.JpegToPng(index.GetJpegBytes(rom, bg.Index));
                    File.WriteAllBytes(Path.Combine(outDir, $"bg{bg.Index:D4}.png"), png);
                }
                Console.WriteLine($"exported {targets.Count():N0} PNG(s) to {Path.GetFullPath(outDir)}");
                return 0;
            }

            case "replace":
            {
                int target = int.Parse(Option(args, "--index") ?? throw new ArgumentException("Missing --index."));
                string image = Option(args, "--image") ?? throw new ArgumentException("Missing --image.");
                string outRom = Option(args, "--out") ?? throw new ArgumentException("Missing --out.");

                var bg = index[target];
                var encoded = BackgroundCodec.EncodeReplacement(image, bg);

                Console.WriteLine($"background {target}: {bg.Width}x{bg.Height}, slot {bg.SlotCapacity:N0} bytes");
                Console.WriteLine($"encoded at quality {encoded.Quality} -> {encoded.Size:N0} bytes");

                if (!encoded.FitsSlot)
                    return Fail($"Encoded image is {encoded.Size:N0} bytes but the slot holds {bg.SlotCapacity:N0}. " +
                                "Use a simpler image, or wait for ROM expansion support.");

                if (!BackgroundCodec.IsGameDecodable(encoded.Data, out string why))
                    return Fail($"Encoder produced something the game cannot decode: {why}.");

                BackgroundWriter.Replace(rom, bg, encoded.Data);
                rom.Save(outRom);
                Console.WriteLine($"wrote {outRom} (checksum fixed)");
                return 0;
            }

            default:
                return Fail($"Unknown 'bg' subcommand '{sub}'. Expected list, dump, export or replace.");
        }
    }

    private static int CmdDumpCode(string[] args)
    {
        var rom = LoadRom(args);
        string outDir = Option(args, "--out") ?? "code";
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"scanning deflate chain from 0x{Re2RomMap.CodeChainStart:X} ...");
        var segments = CodeSegments.Scan(rom.Data, Re2RomMap.CodeChainStart, Re2RomMap.CodeChainLimit);

        long totalIn = 0, totalOut = 0;
        foreach (var s in segments)
        {
            var data = CodeSegments.Decompress(rom.Data, s);
            File.WriteAllBytes(Path.Combine(outDir, $"seg_{s.Index:D2}.bin"), data);
            totalIn += s.CompressedSize;
            totalOut += s.DecompressedSize;
            Console.WriteLine($"  seg {s.Index:D2}  0x{s.RomOffset:X7}..0x{s.RomEnd:X7}  in {s.CompressedSize,8:N0}  out {s.DecompressedSize,9:N0}  {s.Ratio:0.00}x");
        }

        var manifest = new
        {
            rom = Path.GetFileName(rom.SourcePath),
            chainStart = $"0x{Re2RomMap.CodeChainStart:X}",
            chainEnd = segments.Count > 0 ? $"0x{segments[^1].RomEnd:X}" : null,
            segments = segments.Select(s => new
            {
                index = s.Index,
                romOffset = $"0x{s.RomOffset:X}",
                compressedSize = s.CompressedSize,
                decompressedSize = s.DecompressedSize
            }).ToArray()
        };
        File.WriteAllText(Path.Combine(outDir, "segments.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine($"{segments.Count} segments, {totalIn:N0} -> {totalOut:N0} bytes ({(double)totalOut / totalIn:0.00}x)");
        Console.WriteLine($"chain ends at 0x{(segments.Count > 0 ? segments[^1].RomEnd : 0):X}");
        Console.WriteLine($"written to {Path.GetFullPath(outDir)}");
        return 0;
    }
}
