using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Text.Json.Serialization;
using Re2.Core.Assets;
using Re2.Core.Codecs;
using Re2.Core.Export;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Core.Project;

/// <summary>One extracted blob. Several asset ids may share it.</summary>
public sealed class ProjectBlob
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("kind")] public AssetKind Kind { get; set; }

    /// <summary>
    /// What the asset is -- "background", "model", "texture" -- as opposed to <see cref="Kind"/>, which
    /// is only how it is compressed.
    /// </summary>
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("ids")] public List<int> Ids { get; set; } = new();
    [JsonPropertyName("storedSize")] public int StoredSize { get; set; }
    [JsonPropertyName("declaredSize")] public int DeclaredSize { get; set; }
    [JsonPropertyName("originalOffset")] public int OriginalOffset { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>The byte sitting between an odd-length blob and its trailer.</summary>
    [JsonPropertyName("padByte")] public byte PadByte { get; set; }
}

/// <summary>One sample written out as a playable WAV.</summary>
public sealed class ProjectSoundSample
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

public sealed class ProjectManifest
{
    [JsonPropertyName("rom")] public string Rom { get; set; } = "";
    [JsonPropertyName("gameCode")] public string GameCode { get; set; } = "";

    /// <summary>Which build this was extracted from, as <see cref="Re2Release"/> names it.</summary>
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("assetBase")] public int AssetBase { get; set; }
    [JsonPropertyName("fileCount")] public int FileCount { get; set; }
    [JsonPropertyName("nullIds")] public List<int> NullIds { get; set; } = new();
    [JsonPropertyName("blobs")] public List<ProjectBlob> Blobs { get; set; } = new();
    [JsonPropertyName("soundSamples")] public List<ProjectSoundSample> SoundSamples { get; set; } = new();
}

/// <summary>Extracts a ROM to an editable folder and rebuilds a ROM from it.</summary>
public static class ProjectFolder
{
    public const string ManifestName = "manifest.json";
    public const string BlobDirectory = "assets";

    /// <summary>Where one blob is written and what it is called.</summary>
    internal sealed record BlobName(string Category, string Folder, string FileName)
    {
        public string RelativePath => $"{BlobDirectory}/{Folder}/{FileName}";
    }

    /// <summary>
    /// Decides what each asset is, using exactly the same sources the editor's tabs use, so the two can
    /// never disagree about an asset's name.
    /// </summary>
    public sealed class Classifier
    {
        private readonly Dictionary<int, string> _backgroundByRomOffset = new();
        private readonly Dictionary<int, string> _byAssetId = new();
        private readonly HashSet<int> _meshes = new();
        private readonly HashSet<int> _masks = new();
        private readonly Re2Layout _layout;

        public Classifier(RomFile rom, AssetDirectory directory)
        {
            _layout = rom.Layout;

            // Backgrounds: keyed by ROM offset, which is what the index reports.
            try
            {
                var backgrounds = BackgroundIndex.BuildFromDirectory(rom, directory);
                foreach (var background in backgrounds.Backgrounds)
                    _backgroundByRomOffset[background.Offset] = $"bg{background.Index:D4}";
            }
            catch (Exception) { /* an unreadable index only costs nicer names */ }

            // Sound: five fixed assets, named for what each one is rather than by number.
            _byAssetId[_layout.VoiceBankAsset] = "sequences";
            _byAssetId[_layout.SoundProjectAsset] = "project";
            _byAssetId[_layout.SoundPoolAsset] = "pool";
            _byAssetId[_layout.SampleDirectoryAsset] = "sample-directory";
            _byAssetId[_layout.SampleDataAsset] = "sample-data";

            // Masks: the foreground pieces each camera draws over the background.
            try
            {
                foreach (var room in MaskTable.Read(rom))
                    foreach (int id in room)
                        if (id >= 0) _masks.Add(id);
            }
            catch (Exception) { /* as above */ }

            // Characters: the mesh and animation assets the Characters tab lists.
            try
            {
                var overlay = ModelTextureTable.LoadMainOverlay(rom);
                var seen = new HashSet<int>();
                for (int table = 0; table < ModelTextureTable.EntityTableAddresses.Length; table++)
                    foreach (var character in ModelTextureTable.ReadCharacters(overlay, table))
                    {
                        if (seen.Add(character.MeshAssetId)) _meshes.Add(character.MeshAssetId);

                        // Slot 0 is the clip table, slot 1 the pose bank; the rest are unnamed.
                        for (int i = 0; i < character.AnimationAssetIds.Count; i++)
                        {
                            int id = character.AnimationAssetIds[i];
                            if (_animations.ContainsKey(id)) continue;
                            _animations[id] = i switch
                            {
                                0 => $"clips{id}",
                                1 => $"poses{id}",
                                _ => $"anim{id}"
                            };
                        }
                    }
            }
            catch (Exception) { /* as above */ }

            // Text: the readable strings the Text tab lists.
            try
            {
                foreach (var entry in TextTable.Read(rom, directory))
                    _text.Add(entry.AssetId);
            }
            catch (Exception) { /* as above */ }
        }

        private readonly Dictionary<int, string> _animations = new();
        private readonly HashSet<int> _text = new();

        /// <summary>
        /// The category an asset would be filed under if the project were extracted right now.
        /// </summary>
        public string CategoryOf(AssetDirectory directory, IReadOnlyList<int> assetIds,
                                 ReadOnlySpan<byte> decoded)
            => NameOf(directory, assetIds, decoded) is { } name ? name.Category : "other";

        /// <summary>
        /// The category and the path an asset would be given if the project were extracted now,
        /// as (category, relativePath). Null when the asset is not in the directory.
        /// </summary>
        public (string Category, string Path)? PlaceOf(AssetDirectory directory,
                                                       IReadOnlyList<int> assetIds,
                                                       ReadOnlySpan<byte> decoded)
            => NameOf(directory, assetIds, decoded) is { } name
                ? (name.Category, name.RelativePath)
                : null;

        private BlobName? NameOf(AssetDirectory directory, IReadOnlyList<int> assetIds,
                                 ReadOnlySpan<byte> decoded)
        {
            var entries = new List<AssetEntry>(assetIds.Count);
            foreach (int id in assetIds)
            {
                var entry = directory.Entries.FirstOrDefault(e => e.Index == id);
                if (entry is not null) entries.Add(entry);
            }

            return entries.Count == 0 ? null : Name(entries, decoded);
        }

        internal BlobName Name(IReadOnlyList<AssetEntry> entries, ReadOnlySpan<byte> decoded)
        {
            var first = entries[0];

            if (_backgroundByRomOffset.TryGetValue(first.RomOffset, out var background))
                return new BlobName("background", "backgrounds", background + ".jpg");

            foreach (var entry in entries)
                if (_byAssetId.TryGetValue(entry.Index, out var sound))
                {
                    // The voice bank shares the sounds folder -- it is stored beside the sample
                    // bank and a project's layout should not change under people -- but it is the
                    // Dialogue tab's asset, so the Overrides list says so rather than calling the
                    // whole of the game's speech "sound".
                    string category = entry.Index == _layout.VoiceBankAsset ? "dialogue" : "sound";
                    return new BlobName(category, "sounds", sound + ".bin");
                }

            foreach (var entry in entries)
                if (_masks.Contains(entry.Index))
                    return new BlobName("mask", "masks", $"mask{entry.Index}.bin");

            foreach (var entry in entries)
                if (_meshes.Contains(entry.Index))
                    return new BlobName("model", "models", $"mesh{entry.Index}.mesh");

            foreach (var entry in entries)
                if (_animations.TryGetValue(entry.Index, out var animation))
                    return new BlobName("animation", "animations", animation + ".anim");

            foreach (var entry in entries)
                if (_text.Contains(entry.Index))
                    return new BlobName("text", "text", $"text{entry.Index}.txt");

            // Japan's document pages: pictures of text, kept in their coded form.
            foreach (var entry in entries)
                if (_layout.IsDocumentPage(entry.Index))
                    return new BlobName("text", "text", $"page{entry.Index}.bin");

            // Inventory icons: headerless, so they can only be known by where they are.
            foreach (var entry in entries)
                if (InventoryIcons.IsIconAsset(entry.Index, _layout))
                    return new BlobName("icon", "icons", $"icon{entry.Index}.bin");

            var bytes = decoded.ToArray();

            if (TextureFile.TryParse(bytes, out _))
                return new BlobName("texture", "textures", $"texture{first.Index}.tex");

            // Meshes the character tables never mention: still models, just unnamed by the game.
            if (MeshFile.TryParse(bytes, out _))
                return new BlobName("model", "models", $"mesh{first.Index}.mesh");

            // Movies.
            if (FmvIndex.IsMovie(bytes))
            {
                string name = FmvIndex.ReadName(bytes);
                string file = name.Length > 0
                    ? System.IO.Path.GetFileNameWithoutExtension(name) + ".m2v"
                    : $"fmv{first.Index}.m2v";

                return new BlobName("movie", "movies", file);
            }

            // Menu screens: the Menus tab's images, found the same way it finds them.
            if (MenuImage.TryParse(bytes, out _))
                return new BlobName("menu", "menus", $"menu{first.Index}.bin");

            // Everything still unidentified keeps the old shape: padded id plus how it is stored.
            return new BlobName("other", "other", $"{first.Index:D5}_{first.Kind}.bin");
        }
    }

    /// <summary>
    /// Whether a project extracted from one release can be used with another at all. The two USA
    /// builds share their asset numbering; Europe's and Japan's each differ throughout, so another
    /// region's project would land its files on the wrong assets.
    /// </summary>
    public static bool SameNumbering(Re2Release a, Re2Release b) => Family(a) == Family(b);

    private static Re2Release Family(Re2Release release)
        => release is Re2Release.Europe or Re2Release.Japan ? release : Re2Release.UsaRev1;

    private static Re2Release FamilyOfGameCode(string code) => code switch
    {
        "NREP" => Re2Release.Europe,
        "NB5J" => Re2Release.Japan,
        _ => Re2Release.UsaRev1
    };

    /// <summary>
    /// False when the project in <paramref name="folder"/> was extracted from a release numbered
    /// differently from <paramref name="rom"/>. A project with no readable release is given the
    /// benefit of the doubt.
    /// </summary>
    public static bool FitsRom(string folder, RomFile rom)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(
                SharedFile.ReadAllText(Path.Combine(folder, ManifestName)));
            if (manifest is null) return true;

            var current = Re2Version.Detect(rom).Release;
            if (Enum.TryParse<Re2Release>(manifest.Release, out var extracted) && extracted != Re2Release.Unknown)
                return SameNumbering(extracted, current);

            // Projects from before the release was recorded still name the cart's game code.
            return manifest.GameCode is not { Length: 4 }
                   || FamilyOfGameCode(manifest.GameCode) == Family(current);
        }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// A warning when a project is about to be built onto a different release than it was extracted
    /// from, or null when the two agree (or the project predates the field).
    /// </summary>
    public static string? ReleaseMismatch(ProjectManifest manifest, RomFile rom)
    {
        if (!Enum.TryParse<Re2Release>(manifest.Release, out var extracted) || extracted == Re2Release.Unknown)
            return null;

        var current = Re2Version.Detect(rom);
        if (current.Release == Re2Release.Unknown || current.Release == extracted) return null;

        string was = Re2Version.NameOf(extracted);
        return $"WARNING: this project was extracted from the {was} build, but the ROM open now is " +
               $"{current}. The two are not interchangeable -- extract again from this ROM, or build " +
               $"against the {was} one.";
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    // ---- extract ----------------------------------------------------------

    public static ProjectManifest Extract(RomFile rom, string folder, Action<int, int>? progress = null)
    {
        var directory = AssetDirectory.Read(rom);
        Directory.CreateDirectory(Path.Combine(folder, BlobDirectory));

        var classifier = new Classifier(rom, directory);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var manifest = new ProjectManifest
        {
            Rom = Path.GetFileName(rom.SourcePath) ?? "",
            GameCode = rom.GameCode,
            Release = Re2Version.Detect(rom).Release.ToString(),
            AssetBase = directory.AssetBaseRomOffset,
            FileCount = directory.DeclaredFileCount
        };

        var present = new HashSet<int>(directory.Entries.Select(e => e.Index));
        for (int id = 0; id < directory.DeclaredFileCount; id++)
            if (!present.Contains(id)) manifest.NullIds.Add(id);

        // Group ids that share one blob, keyed by the offset they all point at.
        var groups = new Dictionary<int, List<AssetEntry>>();
        foreach (var entry in directory.Entries)
        {
            if (!groups.TryGetValue(entry.Offset, out var list)) groups[entry.Offset] = list = new List<AssetEntry>();
            list.Add(entry);
        }

        int done = 0;
        foreach (var (offset, entries) in groups.OrderBy(g => g.Key))
        {
            var first = entries[0];

            // Decoded bytes when we understand the codec, stored bytes otherwise.
            byte[] data = directory.TryGetData(rom, first, out var decoded)
                ? decoded
                : rom.Slice(first.RomOffset, first.StoredSize).ToArray();

            var blobName = classifier.Name(entries, data);
            string name = blobName.RelativePath;

            // Two blobs should never want the same name, but a duplicate would silently overwrite an
            // asset and produce a project that rebuilds wrongly, so make it impossible rather than
            // unlikely.
            if (!usedNames.Add(name))
            {
                name = $"{BlobDirectory}/{blobName.Folder}/" +
                       $"{Path.GetFileNameWithoutExtension(blobName.FileName)}_{first.Index:D5}" +
                       Path.GetExtension(blobName.FileName);
                usedNames.Add(name);
            }

            string full = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            WritePatiently(full, data);

            manifest.Blobs.Add(new ProjectBlob
            {
                File = name,
                Kind = first.Kind,
                Category = blobName.Category,
                Ids = entries.Select(e => e.Index).OrderBy(i => i).ToList(),
                StoredSize = first.StoredSize,
                DeclaredSize = first.DecompressedSize,
                OriginalOffset = offset,
                Sha256 = Hash(data),
                PadByte = (first.StoredSize & 1) != 0 && first.RomOffset + first.StoredSize < rom.Length
                    ? rom.Data[first.RomOffset + first.StoredSize]
                    : (byte)0
            });

            progress?.Invoke(++done, groups.Count);
        }

        ExtractSoundSamples(rom, directory, folder, manifest);
        ExtractOverlayText(rom, folder);

        File.WriteAllText(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(manifest, Json));
        return manifest;
    }

    /// <summary>Writes the item names and their examine text out as plain files.</summary>
    private static void ExtractOverlayText(RomFile rom, string folder)
    {
        try
        {
            var overlay = ModelTextureTable.LoadMainOverlay(rom);
            ItemNameFile.Write(folder, ItemNames.Read(overlay));
            ItemTextFile.Write(folder, ItemMessages.Read(overlay));

            if (rom.Layout.AlternateInventoryText is { } alt)
            {
                ItemNameFile.Write(folder, ItemNames.Read(overlay, alternate: true), alt.FileSuffix);
                ItemTextFile.Write(folder, ItemMessages.Read(overlay, alternate: true), alt.FileSuffix);
            }
        }
        catch (Exception) { /* the rest of the project is still worth having */ }
    }

    /// <summary>Writes every sample as a WAV next to the bank it came from.</summary>
    private static void ExtractSoundSamples(RomFile rom, AssetDirectory directory, string folder,
                                            ProjectManifest manifest)
    {
        try
        {
            var bank = SoundDirectory.Read(rom, directory);
            string relative = $"{BlobDirectory}/sounds/samples";
            string target = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(target);

            foreach (var sample in bank.Samples)
            {
                byte[] wav = bank.ToWav(sample);
                string name = $"snd{sample.Index:D4}_{sample.SampleRate}hz.wav";
                File.WriteAllBytes(Path.Combine(target, name), wav);

                manifest.SoundSamples.Add(new ProjectSoundSample
                {
                    File = $"{relative}/{name}",
                    Index = sample.Index,
                    Sha256 = Hash(wav)
                });
            }
        }
        catch (Exception)
        {
            manifest.SoundSamples.Clear();
        }
    }

    /// <summary>Rewrites the project's sample WAVs from the ROM and re-records their hashes.</summary>
    public static int RestoreSoundSamples(RomFile rom, string folder, ProjectManifest manifest)
    {
        if (manifest.SoundSamples.Count == 0) return 0;

        var directory = AssetDirectory.Read(rom);
        var bank = SoundDirectory.Read(rom, directory);
        int restored = 0;

        foreach (var record in manifest.SoundSamples)
        {
            var sample = bank.Samples.FirstOrDefault(s => s.Index == record.Index);
            if (sample is null) continue;

            byte[] wav = bank.ToWav(sample);
            string hash = Hash(wav);
            if (hash == record.Sha256) continue;

            string path = Path.Combine(folder, record.File.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, wav);

            record.Sha256 = hash;
            restored++;
        }

        return restored;
    }

    /// <summary>Folds edited WAVs back into the two assets that hold the sample bank.</summary>
    public static Dictionary<int, byte[]> RebuiltSoundAssets(RomFile baseRom, string folder,
                                                             ProjectManifest manifest)
    {
        var result = new Dictionary<int, byte[]>();
        if (manifest.SoundSamples.Count == 0) return result;

        var replacements = new List<SoundBankBuilder.Replacement>();

        foreach (var sample in manifest.SoundSamples)
        {
            string path = Path.Combine(folder, sample.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;

            byte[] wav;
            try { wav = SharedFile.ReadAllBytes(path); }
            catch (IOException) { continue; }

            if (Hash(wav) == sample.Sha256) continue;

            try
            {
                var (pcm, rate) = WavCodec.Read(wav);
                replacements.Add(new SoundBankBuilder.Replacement(sample.Index, pcm, rate));
            }
            catch (Exception)
            {
                // A WAV we cannot read is left out rather than taking down the build; the sample
                // simply keeps its original audio.
            }
        }

        if (replacements.Count == 0) return result;

        var bank = SoundDirectory.Read(baseRom, AssetDirectory.Read(baseRom));
        var (table, data) = SoundBankBuilder.Rebuild(bank, replacements);

        result[baseRom.Layout.SampleDirectoryAsset] = table;
        result[baseRom.Layout.SampleDataAsset] = data;
        return result;
    }

    // ---- build ------------------------------------------------------------

    /// <summary>Writes a file, giving way briefly to whatever else is holding it.</summary>
    internal static void WritePatiently(string path, byte[] data)
    {
        const int Attempts = 5;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllBytes(path, data);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < Attempts)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    /// <summary>
    /// <paramref name="ByteIdentical"/> covers the asset region only, which is what the rest of the
    /// build writes.
    /// </summary>
    public sealed record BuildResult(int BlobsRebuilt, int BytesUsed, int BytesFree, bool ByteIdentical,
                                     int AssetsMoved, bool ItemNamesApplied = false,
                                     bool ItemTextApplied = false, string OverlayTextError = "");

    /// <summary>
    /// Rebuilds the asset region from a project folder, writing into a copy of the base ROM.
    /// </summary>
    public static BuildResult Build(RomFile baseRom, string folder, RomFile output,
                                    IReadOnlySet<int>? forceRebuild = null)
    {
        string manifestPath = Path.Combine(folder, ManifestName);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("No manifest.json in " + folder);

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(SharedFile.ReadAllText(manifestPath))
                       ?? throw new InvalidDataException("manifest.json could not be read.");

        int assetBase = manifest.AssetBase;
        int tableSize = AssetDirectory.HeaderSize + manifest.FileCount * AssetDirectory.RecordSize;

        // Edited WAVs are folded back into the bank before the blobs are laid out.
        var fromSound = RebuiltSoundAssets(baseRom, folder, manifest);

        var blobs = manifest.Blobs.OrderBy(b => b.OriginalOffset).ToList();
        var payloads = new List<(ProjectBlob Blob, byte[] Stored, int DeclaredSize)>(blobs.Count);
        int rebuilt = 0;

        foreach (var blob in blobs)
        {
            string path = Path.Combine(folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
            byte[] current = File.Exists(path) ? SharedFile.ReadAllBytes(path) : Array.Empty<byte>();

            bool forced = forceRebuild is not null && blob.Ids.Any(forceRebuild.Contains);

            if (current.Length > 0 && (forced || Hash(current) != blob.Sha256))
            {
                var (stored, declared) = Encode(blob.Kind, current);
                payloads.Add((blob, stored, declared));
                rebuilt++;
            }
            else if (blob.Ids.FirstOrDefault(id => fromSound.ContainsKey(id)) is var soundId && soundId != 0
                     && fromSound.TryGetValue(soundId, out var soundBytes))
            {
                var (stored, declared) = Encode(blob.Kind, soundBytes);
                payloads.Add((blob, stored, declared));
                rebuilt++;
            }
            else
            {
                // Untouched: copy the original stored bytes so an unedited rebuild is byte-identical.
                int at = assetBase + blob.OriginalOffset;
                payloads.Add((blob, baseRom.Slice(at, blob.StoredSize).ToArray(), blob.DeclaredSize));
            }
        }

        // Lay the region out.
        int available = output.Length - assetBase - tableSize;
        var offsets = new Dictionary<int, int>();

        var region = LayOut(compact: false, out int end, out int moved)
                     ?? LayOut(compact: true, out end, out moved)
                     ?? throw new InvalidOperationException(Shortfall());

        // Saying only "it does not fit" leaves the one useful number out.
        string Shortfall()
        {
            long need = 0;
            foreach (var (_, stored, _) in payloads)
            {
                int size = stored.Length + (stored.Length & 1) + AssetDirectory.TrailerSize;
                need += size + (size & 1);
            }

            return $"Rebuilt assets need {need:N0} bytes but only {available:N0} are available -- " +
                   $"{need - available:N0} too many, even after compacting the region. Shrink an asset. " +
                   "Growing the ROM past the retail 64 MB is implemented (RomFile.Expand) but " +
                   "deliberately not offered: oversized carts are not known to run.";
        }

        byte[]? LayOut(bool compact, out int regionEnd, out int relocated)
        {
            static int Need(byte[] stored) => stored.Length + (stored.Length & 1) + AssetDirectory.TrailerSize;

            offsets.Clear();
            relocated = 0;

            var buffer = new byte[available];

            // The slot a blob may grow into runs to wherever the next one starts, since that one is
            // staying put. The last blob's slot ends at the region's original end.
            int originalEnd = 0;
            foreach (var (blob, _, _) in payloads)
                originalEnd = Math.Max(originalEnd, blob.OriginalOffset + blob.StoredSize
                                                    + (blob.StoredSize & 1) + AssetDirectory.TrailerSize);

            int tail = compact ? tableSize : originalEnd + (originalEnd & 1);
            regionEnd = compact ? tableSize : originalEnd;

            for (int i = 0; i < payloads.Count; i++)
            {
                var (blob, stored, declared) = payloads[i];
                int need = Need(stored);

                int slotEnd = i + 1 < payloads.Count ? payloads[i + 1].Blob.OriginalOffset : originalEnd;

                int at;
                if (compact || blob.OriginalOffset + need > slotEnd)
                {
                    at = tail;
                    tail += need + (need & 1);
                    if (!compact) relocated++;
                }
                else
                {
                    at = blob.OriginalOffset;
                }

                int w = at - tableSize;
                if (w < 0 || w + need > buffer.Length) return null;

                foreach (int id in blob.Ids) offsets[id] = at;

                stored.CopyTo(buffer, w);
                w += stored.Length;
                if ((stored.Length & 1) != 0) buffer[w++] = blob.PadByte;

                BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(w), (uint)blob.Kind << 16);
                BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(w + 4), (uint)declared);

                regionEnd = Math.Max(regionEnd, at + need);
            }

            if (compact) relocated = payloads.Count(p => offsets.TryGetValue(p.Blob.Ids[0], out int o)
                                                         && o != p.Blob.OriginalOffset);
            return buffer;
        }


        // Header and directory table.
        var table = new byte[tableSize];
        Array.Copy(baseRom.Data, assetBase, table, 0, AssetDirectory.HeaderSize);

        foreach (int id in manifest.NullIds)
        {
            int at = AssetDirectory.HeaderSize + id * AssetDirectory.RecordSize;
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(at), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(at + 4), 0);
        }

        foreach (var (id, offset) in offsets)
        {
            int at = AssetDirectory.HeaderSize + id * AssetDirectory.RecordSize;
            var blob = payloads.First(p => p.Blob.Ids.Contains(id));
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(at), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(at + 4), (uint)blob.Stored.Length);
        }

        table.CopyTo(output.Data, assetBase);
        Array.Copy(region, 0, output.Data, assetBase + tableSize, end - tableSize);

        // Zero whatever the old ROM had past the new end, so leftovers cannot be mistaken for data.
        int romEnd = assetBase + end;
        Array.Clear(output.Data, romEnd, output.Length - romEnd);

        bool identical = output.Data.AsSpan(assetBase).SequenceEqual(baseRom.Data.AsSpan(assetBase));

        // The item names go in last, and into the overlay rather than the region laid out above.
        var text = OverlayText.Result.Nothing;
        try { text = OverlayText.Apply(folder, output); }
        catch (Exception ex) { text = new OverlayText.Result(false, false, ex.Message); }

        return new BuildResult(rebuilt, romEnd, output.Length - romEnd, identical, moved,
                               text.NamesApplied, text.MessagesApplied, text.Error);
    }

    /// <summary>Turns decoded bytes back into the form the container stores.</summary>
    private static (byte[] Stored, int DeclaredSize) Encode(AssetKind kind, byte[] decoded) => kind switch
    {
        AssetKind.Stored => (decoded, decoded.Length),

        // A complete zlib stream: header, deflate, and the Adler-32 the console's zlib checks.
        AssetKind.Deflate => (Zlib.Compress(decoded), decoded.Length),

        AssetKind.Rle => (PackBits.EncodeBytes(decoded), decoded.Length),

        // A 16-bit payload with an odd length cannot exist; fall back rather than throw, so one
        // malformed edit cannot take down a whole build.
        AssetKind.Rle16 when (decoded.Length & 1) == 0 => (PackBits.EncodeUnits(decoded), decoded.Length),

        _ => (decoded, decoded.Length)
    };

}
