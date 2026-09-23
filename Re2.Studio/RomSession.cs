using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Assets;
using Re2.Core.Emulation;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Studio;

/// <summary>Everything the UI needs from one ROM, loaded lazily.</summary>
public sealed class RomSession : IDisposable
{
    public RomFile Rom { get; }
    public string Path { get; }
    public AssetDirectory Assets { get; }
    public BackgroundIndex Backgrounds { get; }

    private readonly Dictionary<int, AssetEntry> _byId;

    /// <summary>Edits sitting in a project folder.</summary>
    public ProjectOverrides Overrides { get; } = new();

    private List<(AssetEntry Entry, TextureFile Texture)>? _textures;
    private List<ModelEntry>? _characters;
    private SoundDirectory? _sounds;
    private ModelTextureTable.Overlay? _overlay;

    public RomSession(string path)
    {
        Path = path;
        Rom = RomFile.Load(path);
        Assets = AssetDirectory.Read(Rom);
        Backgrounds = BackgroundIndex.Build(Rom);
        _byId = Assets.Entries.ToDictionary(e => e.Index);
        Assets.OverrideProvider = Overrides.Provide;
    }

    public ModelTextureTable.Overlay Overlay => _overlay ??= ModelTextureTable.LoadMainOverlay(Rom);

    public bool TryGetAsset(int id, out byte[] data)
    {
        data = Array.Empty<byte>();
        return _byId.TryGetValue(id, out var entry) && Assets.TryGetData(Rom, entry, out data);
    }

    /// <summary>An asset exactly as the cart holds it, with no project edit applied, and its stored size.</summary>
    public bool TryGetCartAsset(int id, out byte[] data, out int storedSize)
    {
        data = Array.Empty<byte>();
        storedSize = 0;
        if (!_byId.TryGetValue(id, out var entry)) return false;
        storedSize = entry.StoredSize;
        return Assets.TryGetCartData(Rom, entry, out data);
    }

    /// <summary>All texture assets, indexed once on first use.</summary>
    public IReadOnlyList<(AssetEntry Entry, TextureFile Texture)> Textures
    {
        get
        {
            if (_textures is not null) return _textures;

            _textures = new List<(AssetEntry, TextureFile)>();
            foreach (var entry in Assets.Entries)
            {
                if (!Assets.TryGetData(Rom, entry, out var data)) continue;
                if (TextureFile.TryParse(data, out var texture)) _textures.Add((entry, texture));
            }

            return _textures;
        }
    }

    /// <summary>The menu screens: inventory, map, file viewer.</summary>
    public IReadOnlyList<(AssetEntry Entry, MenuImage Image)> MenuImages
    {
        get
        {
            if (_menuImages is not null) return _menuImages;

            _menuImages = new List<(AssetEntry, MenuImage)>();
            foreach (var entry in Assets.Entries)
            {
                if (!Assets.TryGetData(Rom, entry, out var data)) continue;
                if (MenuImage.TryParse(data, out var image)) _menuImages.Add((entry, image!));
            }

            return _menuImages;
        }
    }

    private List<(AssetEntry, MenuImage)>? _menuImages;

    /// <summary>Characters from every scenario table, de-duplicated by mesh.</summary>
    public IReadOnlyList<ModelEntry> Characters
    {
        get
        {
            if (_characters is not null) return _characters;

            _characters = new List<ModelEntry>();
            var seen = new HashSet<int>();

            for (int table = 0; table < ModelTextureTable.EntityTableAddresses.Length; table++)
                foreach (var character in ModelTextureTable.ReadCharacters(Overlay, table))
                    if (seen.Add(character.MeshAssetId))
                        _characters.Add(character);

            // The item table opens with the player models, and the entity tables do not reach all of
            // them -- the alternate costumes are named only there.
            foreach (var player in ItemTable.Read(Overlay))
            {
                if (player.Index >= ItemTable.FirstItemSlot) break;
                if (seen.Add(player.MeshAssetId)) _characters.Add(player);
            }

            return _characters;
        }
    }

    /// <summary>
    /// Item models -- weapons, ammunition, herbs and the rest -- from the table that sits between the
    /// character asset table and the pair table.
    /// </summary>
    public IReadOnlyList<ModelEntry> Items
        => _items ??= ItemTable.Read(Overlay)
                               .Where(entry => entry.Index >= ItemTable.FirstItemSlot)
                               .ToList();

    private List<ModelEntry>? _items;

    /// <summary>
    /// Scenery models -- the crates, statues and machinery rooms place -- with the texture set the room
    /// that places them binds.
    /// </summary>
    public IReadOnlyList<ModelEntry> Scenery => _scenery ??= BuildScenery();

    private List<ModelEntry>? _scenery;

    private List<ModelEntry> BuildScenery()
    {
        var models = new HashSet<int>();
        foreach (var entry in Assets.Entries)
            if (TryGetAsset(entry.Index, out var data) && MeshFile.TryParse(data, out _))
                models.Add(entry.Index);

        // Whatever the character and item tables already account for belongs on their own tabs.
        var claimed = new HashSet<int>();
        foreach (var character in Characters) claimed.Add(character.MeshAssetId);
        foreach (var item in Items) claimed.Add(item.MeshAssetId);

        var byMesh = new Dictionary<int, ModelTextureSet>();
        foreach (var prop in RoomPropTable.Read(Overlay, models))
        {
            if (byMesh.ContainsKey(prop.MeshAssetId)) continue;
            if (ModelTextureTable.ReadPair(Overlay, prop.PairIndex) is { } textures)
                byMesh[prop.MeshAssetId] = textures;
        }

        var empty = new ModelTextureSet(-1, 0, Array.Empty<int>(), Array.Empty<int>());
        var scenery = new List<ModelEntry>();

        foreach (int mesh in models.Where(id => !claimed.Contains(id)).OrderBy(id => id))
        {
            // Only what a room actually places, plus the strays sitting among them.
            if (!byMesh.TryGetValue(mesh, out var textures))
            {
                if (mesh < SceneryFirstAssetId || mesh > SceneryLastAssetId) continue;
                textures = empty;
            }

            scenery.Add(new ModelEntry(scenery.Count, textures.PairIndex, 0, mesh,
                                       Array.Empty<int>(), textures));
        }

        return scenery;
    }

    /// <summary>The run of asset ids the scenery models occupy.</summary>
    private const int SceneryFirstAssetId = 7875;
    private const int SceneryLastAssetId = 8083;

    /// <summary>The voice bank: cutscene and event dialogue, 588 clips.</summary>
    public IReadOnlyList<VoiceClip> Voices => _voices ??= VoiceBank.Read(Rom, Assets);

    private IReadOnlyList<VoiceClip>? _voices;

    /// <summary>The prerendered movies: MPEG-1 streams, found by their sequence headers.</summary>
    public IReadOnlyList<FmvStream> Movies => _movies ??= FmvIndex.Build(Rom, Assets);

    private IReadOnlyList<FmvStream>? _movies;

    /// <summary>One movie's bytes, through the project's overrides like everything else.</summary>
    public byte[] MovieData(FmvStream movie)
        => TryGetAsset(movie.AssetId, out var data) ? data : Array.Empty<byte>();

    /// <summary>The raw voice bank, which clips are offsets into.</summary>
    public byte[] VoiceBankData
    {
        get
        {
            if (_voiceBank is not null) return _voiceBank;

            foreach (var entry in Assets.Entries)
                if (entry.Index == VoiceBank.AssetId && Assets.TryGetData(Rom, entry, out var data))
                    return _voiceBank = data;

            return _voiceBank = Array.Empty<byte>();
        }
    }

    private byte[]? _voiceBank;

    /// <summary>Decoded audio for one clip, kept once decoded.</summary>
    public short[] DecodeVoice(VoiceClip clip)
    {
        if (_decodedVoices.TryGetValue(clip.Index, out var cached)) return cached;

        var bank = VoiceBankData;
        if (bank.Length == 0) return Array.Empty<short>();

        var samples = MortCodec.Decode(VoiceCpu, bank.AsSpan(clip.Offset, clip.StoredSize), clip.BlockCount);
        _decodedVoices[clip.Index] = samples;
        return samples;
    }

    /// <summary>The interpreter the voice codec runs on, built once because it costs an overlay decompression.</summary>
    public MipsCpu VoiceCpu => _voiceCpu ??= GameCode.Load(Rom);

    private MipsCpu? _voiceCpu;
    private readonly Dictionary<int, short[]> _decodedVoices = new();

    public SoundDirectory Sounds => _sounds ??= SoundDirectory.Read(Rom, Assets);

    /// <summary>The bank as the cart holds it, with no project edit applied.</summary>
    public SoundDirectory CartSounds
    {
        get
        {
            if (_cartSounds is not null) return _cartSounds;

            if (!TryGetCartAsset(SoundDirectory.SampleDirectoryAssetId, out var table, out _) ||
                !TryGetCartAsset(SoundDirectory.SampleDataAssetId, out var data, out _))
                return _cartSounds = Sounds;

            return _cartSounds = SoundDirectory.ReadFrom(table, data);
        }
    }

    private SoundDirectory? _cartSounds;

    /// <summary>Throws away everything decoded from the old view of the ROM.</summary>
    public void InvalidateCaches()
    {
        _textures = null;
        _menuImages = null;
        _characters = null;
        _items = null;
        _scenery = null;
        _voices = null;
        _movies = null;
        _rooms = null;
        _roomMasks = null;
        _entrances = null;
        _decodedMasks.Clear();
        _backgroundCamera = null;
        _assetToBackground = null;
        _voiceBank = null;
        _decodedVoices.Clear();
        _sounds = null;
        _backgroundAssetIds = null;
    }

    /// <summary>The JPEG for a background, preferring a project edit over the cart.</summary>
    public byte[] GetBackgroundJpeg(int backgroundIndex)
    {
        if (TryGetBackgroundAssetId(backgroundIndex, out int assetId)
            && Overrides.Provide(assetId) is { } replacement)
            return replacement;

        return Backgrounds.GetJpegBytes(Rom, backgroundIndex).ToArray();
    }

    /// <summary>The asset id a background lives in.</summary>
    public bool TryGetBackgroundAssetId(int backgroundIndex, out int assetId)
    {
        _backgroundAssetIds ??= BuildBackgroundAssetIds();
        return _backgroundAssetIds.TryGetValue(backgroundIndex, out assetId);
    }

    private Dictionary<int, int>? _backgroundAssetIds;

    /// <summary>The rooms, with the backgrounds each of their camera angles draws.</summary>
    public IReadOnlyList<RoomEntry> Rooms => _rooms ??= RoomTable.Read(Overlay);

    private List<RoomEntry>? _rooms;

    /// <summary>
    /// Foreground mask asset ids per room, in camera order; -1 where a camera has nothing in front
    /// of the player. Indexed the same way <see cref="Rooms"/> is.
    /// </summary>
    public IReadOnlyList<int[]> RoomMasks => _roomMasks ??= MaskTable.Read(Overlay);

    private List<int[]>? _roomMasks;

    /// <summary>The mask for one camera, decoded and kept. Null when it has none or will not parse.</summary>
    public MaskFile? LoadMask(int roomIndex, int camera)
    {
        var masks = RoomMasks;
        if (roomIndex < 0 || roomIndex >= masks.Count) return null;
        if (camera < 0 || camera >= masks[roomIndex].Length) return null;

        int assetId = masks[roomIndex][camera];
        if (assetId < 0) return null;

        if (_decodedMasks.TryGetValue(assetId, out var cached)) return cached;

        MaskFile? mask = null;
        if (TryGetAsset(assetId, out var data)) MaskFile.TryParse(data, out mask);

        _decodedMasks[assetId] = mask;
        return mask;
    }

    private readonly Dictionary<int, MaskFile?> _decodedMasks = new();

    /// <summary>The foreground for a background, found by the camera that draws it.</summary>
    public MaskFile? MaskForBackground(int backgroundIndex)
    {
        if (_backgroundCamera is null)
        {
            _backgroundCamera = new Dictionary<int, (int, int)>();

            foreach (var room in Rooms)
            {
                for (int camera = 0; camera < room.ViewCount; camera++)
                {
                    int index = BackgroundIndexOfAsset(room.BackgroundAssetIds[camera]);
                    if (index >= 0 && !_backgroundCamera.ContainsKey(index))
                        _backgroundCamera[index] = (room.FlatIndex, camera);
                }
            }
        }

        return _backgroundCamera.TryGetValue(backgroundIndex, out var at) ? LoadMask(at.Item1, at.Item2) : null;
    }

    /// <summary>
    /// The asset id holding a background's foreground, or -1 when that camera has none.
    /// </summary>
    public int MaskAssetForBackground(int backgroundIndex)
    {
        _ = MaskForBackground(backgroundIndex);   // builds the camera map

        if (_backgroundCamera is null || !_backgroundCamera.TryGetValue(backgroundIndex, out var at))
            return -1;

        var masks = RoomMasks;
        if (at.Room >= masks.Count || at.Camera >= masks[at.Room].Length) return -1;

        return masks[at.Room][at.Camera];
    }

    private Dictionary<int, (int Room, int Camera)>? _backgroundCamera;

    /// <summary>
    /// The doors that lead into each room, indexed the same way <see cref="Rooms"/> is.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Door>> Entrances
        => _entrances ??= DoorTable.EntrancesByRoom(Rom).ConvertAll(d => (IReadOnlyList<Door>)d);

    private List<IReadOnlyList<Door>>? _entrances;

    /// <summary>The background list position for an asset id, or -1.</summary>
    public int BackgroundIndexOfAsset(int assetId)
    {
        _assetToBackground ??= BuildBackgroundAssetIds().ToDictionary(pair => pair.Value, pair => pair.Key);
        return _assetToBackground.TryGetValue(assetId, out int index) ? index : -1;
    }

    private Dictionary<int, int>? _assetToBackground;

    private Dictionary<int, int> BuildBackgroundAssetIds()
    {
        var byOffset = new Dictionary<int, int>();
        foreach (var entry in Assets.Entries)
            byOffset.TryAdd(entry.RomOffset, entry.Index);

        var map = new Dictionary<int, int>();
        for (int i = 0; i < Backgrounds.Count; i++)
            if (byOffset.TryGetValue(Backgrounds[i].Offset, out int id))
                map[i] = id;

        return map;
    }

    /// <summary>Decoded mesh for a character, or null when the asset is not a mesh.</summary>
    public MeshFile? LoadMesh(int assetId)
        => TryGetAsset(assetId, out var data) && MeshFile.TryParse(data, out var mesh) ? mesh : null;

    /// <summary>Skeleton and poses for a character, or null when it has no rig.</summary>
    public PoseBank? LoadPoseBank(ModelEntry character)
    {
        if (character.AnimationAssetIds.Count < 2) return null;
        try { return PoseBank.Assemble(Rom, Assets, character.AnimationAssetIds[1]); }
        catch (Exception) { return null; }
    }

    public AnimationSet? LoadAnimations(ModelEntry character)
    {
        if (character.AnimationAssetIds.Count < 1) return null;
        try { return AnimationSet.Decode(Rom, Assets, character.AnimationAssetIds[0]); }
        catch (Exception) { return null; }
    }

    public void Dispose() { }
}
