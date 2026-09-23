using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Re2.Core.Formats;
using Re2.Core.Project;
using Re2.Core.Rom;

namespace Re2.Studio;

/// <summary>Assets edited in a project folder but not yet built into a ROM.</summary>
public sealed class ProjectOverrides : IDisposable
{
    /// <summary>One asset that differs from the cart.</summary>
    public sealed record Item(
        int AssetId,
        IReadOnlyList<int> Ids,
        string Category,
        string File,
        int RomSize,
        int ProjectSize)
    {
        public string SizeChange => ProjectSize == RomSize
            ? $"{ProjectSize:N0} bytes"
            : $"{ProjectSize:N0} bytes, was {RomSize:N0}";
    }

    /// <summary>One scan's results.</summary>
    private sealed record Snapshot(Dictionary<int, byte[]> Bytes, List<Item> Items, string Message)
    {
        public static readonly Snapshot Empty =
            new(new Dictionary<int, byte[]>(), new List<Item>(), "no project scanned yet");
    }

    private volatile Snapshot _snapshot = Snapshot.Empty;
    private readonly HashSet<int> _disabled = new();

    /// <summary>Folder last scanned, so the panel can say where these came from.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>Why the last scan found nothing, when it found nothing.</summary>
    public string Message => Scanning ? "scanning the project..." : _snapshot.Message;

    public IReadOnlyList<Item> Items => _snapshot.Items;

    public int Count => _snapshot.Items.Count;

    /// <summary>True while a scan is running in the background.</summary>
    public bool Scanning => _scan is { IsCompleted: false };

    private Task? _scan;

    /// <summary>
    /// Bumped whenever what the editor should be showing changes, so cached decodes and uploaded
    /// textures can be thrown away exactly when they go stale rather than every frame.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>The master switch: off shows the cart as it is, ignoring every edit.</summary>
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Version++; } }
    }

    private bool _enabled = true;

    public bool IsActive(int assetId)
        => _enabled && !_disabled.Contains(assetId) && _snapshot.Bytes.ContainsKey(assetId);

    public bool IsEnabled(int assetId) => !_disabled.Contains(assetId);

    public void SetEnabled(int assetId, bool enabled)
    {
        bool changed = enabled ? _disabled.Remove(assetId) : _disabled.Add(assetId);
        if (changed) Version++;
    }

    public void SetAllEnabled(bool enabled)
    {
        if (enabled)
        {
            if (_disabled.Count == 0) return;
            _disabled.Clear();
        }
        else
        {
            var all = _snapshot.Items.Select(i => i.AssetId).ToHashSet();
            if (all.SetEquals(_disabled)) return;
            foreach (int id in all) _disabled.Add(id);
        }
        Version++;
    }

    /// <summary>The replacement bytes for an asset, or null when the cart's own should be used.</summary>
    public byte[]? Provide(int assetId)
        => IsActive(assetId) && _snapshot.Bytes.TryGetValue(assetId, out var data) ? data : null;

    private FileSystemWatcher? _watcher;
    private long _changedAtTicks;

    /// <summary>
    /// Whether the folder has changed on disk since the last scan and enough time has passed to be
    /// confident the writing has finished.
    /// </summary>
    public bool NeedsRescan
    {
        get
        {
            long at = Interlocked.Read(ref _changedAtTicks);
            if (at == 0) return false;
            return DateTime.UtcNow.Ticks - at >= DebounceTicks;
        }
    }

    /// <summary>How long the folder has to be quiet before it is read.</summary>
    private const long DebounceTicks = TimeSpan.TicksPerMillisecond * 750;

    /// <summary>
    /// Records that the editor itself has just written to the project, so the next frame rereads it.
    /// </summary>
    public void NoteEdited()
        => Interlocked.Exchange(ref _changedAtTicks,
                                DateTime.UtcNow.Ticks - DebounceTicks - TimeSpan.TicksPerMillisecond);

    /// <summary>Watches a project folder for edits made outside the editor.</summary>
    private void Watch(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        Interlocked.Exchange(ref _changedAtTicks, 0);

        if (!Directory.Exists(folder)) return;

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };

            // These arrive on a background thread; recording the time is all that happens here, and
            // the scan itself runs on the UI thread when it next looks.
            void Touched(object? sender, FileSystemEventArgs e)
                => Interlocked.Exchange(ref _changedAtTicks, DateTime.UtcNow.Ticks);

            watcher.Changed += Touched;
            watcher.Created += Touched;
            watcher.Deleted += Touched;
            watcher.Renamed += (_, _) => Interlocked.Exchange(ref _changedAtTicks, DateTime.UtcNow.Ticks);

            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception)
        {
            // A folder that cannot be watched (a network share, say) just means edits are noticed on
            // the next manual Rescan instead.
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// Stops scanning and lets go of the project folder until the returned handle is disposed.
    /// </summary>
    public IDisposable Pause()
    {
        _paused = true;

        _watcher?.Dispose();
        _watcher = null;

        // Whatever is mid-read has to finish before the caller may write.
        try { _scan?.Wait(); } catch (AggregateException) { }

        return new Resumption(this);
    }

    private volatile bool _paused;

    private sealed class Resumption : IDisposable
    {
        private readonly ProjectOverrides _owner;
        private bool _done;

        public Resumption(ProjectOverrides owner) => _owner = owner;

        public void Dispose()
        {
            if (_done) return;
            _done = true;

            _owner._paused = false;
            _owner.Folder = "";        // force Scan to re-establish the watcher on the next call
            _owner.NoteEdited();       // and to actually run, since the folder's contents just changed
        }
    }

    public void Scan(string folder, RomFile rom)
    {
        if (_paused) return;

        if (folder != Folder) Watch(folder);

        Folder = folder;

        // The pending mark is cleared only once a scan actually starts.
        if (Scanning) return;                     // one at a time; the watcher will ask again

        Interlocked.Exchange(ref _changedAtTicks, 0);

        _scan = Task.Run(() =>
        {
            var result = Build(folder, rom);
            _snapshot = result;
            Version++;                            // read by the drawing thread to drop stale caches
        });
    }

    /// <summary>Blocks until the running scan has finished.</summary>
    public void WaitForScan() => _scan?.Wait();

    private Snapshot Build(string folder, RomFile rom)
    {
        var bytes = new Dictionary<int, byte[]>();
        var items = new List<Item>();

        var (ready, message) = AssetIo.DescribeProject(folder);
        if (!ready) return new Snapshot(bytes, items, message);

        try
        {
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(
                SharedFile.ReadAllText(Path.Combine(folder, ProjectFolder.ManifestName)));
            if (manifest is null)
                return new Snapshot(bytes, items, "manifest.json could not be read");

            // Both built once, and only if something actually needs reclassifying: the classifier
            // decompresses the main overlay, which is far too much work to do for a clean project.
            ProjectFolder.Classifier? classifier = null;
            Re2.Core.Assets.AssetDirectory? directory = null;

            foreach (var blob in manifest.Blobs)
            {
                string path = Path.Combine(folder, blob.File.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;

                byte[] current;
                try { current = SharedFile.ReadAllBytes(path); }
                catch (IOException) { continue; }          // locked by an editor: not an override

                if (Convert.ToHexString(SHA256.HashData(current)) == blob.Sha256) continue;

                // Several ids can share a blob; every one of them shows the edit.
                foreach (int id in blob.Ids) bytes[id] = current;

                // A project records each blob's category when it is extracted, so one extracted before
                // the tool learned a format goes on calling it "other" for ever.
                string category = blob.Category;

                if (category == "other")
                {
                    directory ??= Re2.Core.Assets.AssetDirectory.Read(rom);
                    classifier ??= new ProjectFolder.Classifier(rom, directory);
                    category = classifier.CategoryOf(directory, blob.Ids, current);
                }

                // The voice bank used to be filed with the sample bank as plain "sound".
                if (blob.Ids.Contains(Re2.Core.Assets.VoiceBank.AssetId)) category = "dialogue";

                items.Add(new Item(blob.Ids.Min(), blob.Ids, category, blob.File,
                                   blob.DeclaredSize, current.Length));
            }

            string soundMessage = ScanSoundSamples(rom, folder, manifest, bytes, items);

            items = items.OrderBy(i => i.Category).ThenBy(i => i.AssetId).ToList();

            string summary = items.Count == 0
                ? "nothing edited yet -- the project matches the ROM"
                : $"{items.Count:N0} edited";

            return new Snapshot(bytes, items, soundMessage.Length > 0 ? soundMessage : summary);
        }
        catch (Exception ex)
        {
            return new Snapshot(bytes, items, "could not scan the project: " + ex.Message);
        }
    }

    /// <summary>Picks up sounds edited as WAVs in the project.</summary>
    private string ScanSoundSamples(RomFile rom, string folder, ProjectManifest manifest,
                                    Dictionary<int, byte[]> bytes, List<Item> items)
    {
        if (manifest.SoundSamples.Count == 0) return "";

        try
        {
            var rebuilt = ProjectFolder.RebuiltSoundAssets(rom, folder, manifest);
            if (rebuilt.Count == 0) return "";

            int edited = manifest.SoundSamples.Count(sample =>
            {
                string path = Path.Combine(folder, sample.File.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return false;
                try { return Convert.ToHexString(SHA256.HashData(SharedFile.ReadAllBytes(path))) != sample.Sha256; }
                catch (IOException) { return false; }
            });

            foreach (var (assetId, data) in rebuilt)
            {
                bytes[assetId] = data;

                var blob = manifest.Blobs.FirstOrDefault(b => b.Ids.Contains(assetId));
                items.Add(new Item(assetId, new[] { assetId }, "sound",
                                   blob?.File ?? $"asset {assetId}",
                                   blob?.DeclaredSize ?? 0, data.Length));
            }

            return $"{edited:N0} sample(s) edited";
        }
        catch (Exception)
        {
            // Sound is a convenience on top of the blob comparison; losing it must not lose that.
            return "";
        }
    }
}
