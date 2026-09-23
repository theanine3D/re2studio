using System;
using System.Collections.Generic;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>
/// The table of item models -- weapons, ammunition, herbs, keys and the other things the game shows as
/// objects rather than as characters.
/// </summary>
public static class ItemTable
{
    public const uint TableAddress = 0x801273B0;
    public const int RecordSize = 8;
    public const int AnimationSlots = 2;

    /// <summary>Where the items proper begin.</summary>
    public const int FirstItemSlot = 15;

    private const ushort NoValue = 0xFFFF;

    /// <summary>How many records fit between this table and the pair table that follows it.</summary>
    public static int Count => (int)((ModelTextureTable.PairTableAddress - TableAddress) / RecordSize);

    public static List<ModelEntry> Read(RomFile rom)
        => Read(ModelTextureTable.LoadMainOverlay(rom));

    public static List<ModelEntry> Read(ModelTextureTable.Overlay overlay)
    {
        var items = new List<ModelEntry>(Count);

        for (int i = 0; i < Count; i++)
        {
            uint at = overlay.At(TableAddress) + (uint)i * RecordSize;
            if (!overlay.Contains(at, RecordSize)) break;

            var animations = new List<int>(AnimationSlots);
            for (int slot = 0; slot < AnimationSlots; slot++)
            {
                int id = overlay.U16(at + (uint)slot * 2);
                if (id != NoValue) animations.Add(id);
            }

            int mesh = overlay.U16(at + 4);
            int pairIndex = overlay.U16(at + 6);
            if (mesh == NoValue) continue;

            var textures = ModelTextureTable.ReadPair(overlay, pairIndex);
            if (textures is null) continue;

            items.Add(new ModelEntry(i, pairIndex, 0, mesh, animations, textures));
        }

        return items;
    }
}
