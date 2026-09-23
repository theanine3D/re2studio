using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>The set of textures one model draws from, in texture-index order.</summary>
public sealed record ModelTextureSet(int PairIndex, int Kind, IReadOnlyList<int> TextureIds, IReadOnlyList<int> AlternateTextureIds)
{
    public int Count => TextureIds.Count;
}

/// <summary>
/// One model the game loads as a unit: its mesh, the textures it draws from, and its animation assets.
/// </summary>
public sealed record ModelEntry(
    int Index,
    int PairIndex,
    int AssetBase,
    int MeshAssetId,
    IReadOnlyList<int> AnimationAssetIds,
    ModelTextureSet Textures);

/// <summary>
/// The tables in overlay 1 that tie a character to its mesh, textures and animations.
/// </summary>
public static class ModelTextureTable
{
    public const uint PairTableAddress = 0x801275C0;
    public const uint CharacterAssetTableAddress = 0x80126C80;

    /// <summary>Entity tables, one per scenario. The loader picks between them at runtime.</summary>
    public static readonly uint[] EntityTableAddresses = { 0x801287B0, 0x80128A10, 0x80128C70, 0x80128ED0 };

    public const int EntityRecordSize = 8;
    public const int AssetsPerCharacter = 8;
    public const int MeshSlot = 7;

    /// <summary>Overlay table slot holding the main binary, which carries all of these tables.</summary>
    public const int MainOverlayIndex = 1;

    private const ushort NoValue = 0xFFFF;

    /// <summary>Overlay 1's decompressed image plus the address it loads at.</summary>
    public sealed record Overlay(byte[] Data, uint BaseAddress, int Delta = 0)
    {
        /// <summary>A Rev 1 table address, moved to where this release keeps it.</summary>
        public uint At(uint rev1Address) => (uint)(rev1Address + Delta);

        public bool Contains(uint address, int length)
            => address >= BaseAddress && address - BaseAddress + (uint)length <= (uint)Data.Length;

        public ushort U16(uint address) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan((int)(address - BaseAddress), 2));
    }

    public static Overlay LoadMainOverlay(RomFile rom)
    {
        var entries = OverlayTable.Read(rom.Data);
        var main = entries.First(e => e.Index == MainOverlayIndex);
        if (!OverlayTable.TryDecompress(rom.Data, main, out var data))
            throw new InvalidOperationException("Could not decompress the main overlay.");
        return new Overlay(data, main.LoadAddress, Re2Version.Detect(rom).MainOverlayDelta);
    }

    /// <summary>Reads one packed pair record.</summary>
    public static ModelTextureSet? ReadPair(Overlay overlay, int pairIndex)
    {
        uint at = overlay.At(PairTableAddress) + (uint)pairIndex * 2;
        if (!overlay.Contains(at, 4)) return null;

        int count = overlay.U16(at);
        int kind = overlay.U16(at + 2);
        if (count is <= 0 or > 256) return null;

        int words = 2 + count * (kind == 2 ? 2 : 1);
        if (!overlay.Contains(at, words * 2)) return null;

        var ids = new List<int>(count);
        for (int i = 0; i < count; i++) ids.Add(overlay.U16(at + 4 + (uint)i * 2));

        var alternate = new List<int>();
        if (kind == 2)
            for (int i = 0; i < count; i++) alternate.Add(overlay.U16(at + 4 + (uint)(count + i) * 2));

        return new ModelTextureSet(pairIndex, kind, ids, alternate);
    }

    /// <summary>
    /// Walks one entity table, resolving each slot to its mesh, animations and textures.
    /// </summary>
    public static List<ModelEntry> ReadCharacters(Overlay overlay, int tableIndex = 0, int maxEntries = 128)
    {
        uint table = overlay.At(EntityTableAddresses[tableIndex]);
        var characters = new List<ModelEntry>();

        for (int i = 0; i < maxEntries; i++)
        {
            uint at = table + (uint)i * EntityRecordSize;
            if (!overlay.Contains(at, EntityRecordSize)) break;

            int pairIndex = overlay.U16(at + 4);
            int assetBase = overlay.U16(at + 6);
            if (pairIndex == NoValue || assetBase == NoValue) continue;

            uint assets = overlay.At(CharacterAssetTableAddress) + (uint)assetBase * 2;
            if (!overlay.Contains(assets, AssetsPerCharacter * 2)) continue;

            int meshId = overlay.U16(assets + MeshSlot * 2);
            if (meshId == NoValue) continue;

            var animations = new List<int>();
            for (int slot = 1; slot < MeshSlot; slot++)
            {
                int id = overlay.U16(assets + (uint)slot * 2);
                if (id != NoValue) animations.Add(id);
            }

            var textures = ReadPair(overlay, pairIndex);
            if (textures is null) continue;

            characters.Add(new ModelEntry(i, pairIndex, assetBase, meshId, animations, textures));
        }

        return characters;
    }

    public static List<ModelEntry> ReadCharacters(RomFile rom, int tableIndex = 0)
        => ReadCharacters(LoadMainOverlay(rom), tableIndex);
}
