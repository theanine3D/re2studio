using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Formats;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>Which textures the models still draw, and which are only being carried.</summary>
public sealed record TextureUsageReport(
    IReadOnlyList<int> Listed,
    IReadOnlyList<int> Live,
    IReadOnlyList<int> Dead,
    int ModelsRead,
    int ModelsUnreadable);

/// <summary>Finds textures no model draws any more.</summary>
public static class TextureUsage
{
    /// <summary>Works out the dead set.</summary>
    public static TextureUsageReport Analyse(RomFile rom, Func<int, byte[]?> meshBytes)
    {
        var overlay = ModelTextureTable.LoadMainOverlay(rom);

        var models = new List<ModelEntry>();
        for (int table = 0; table < ModelTextureTable.EntityTableAddresses.Length; table++)
            models.AddRange(ModelTextureTable.ReadCharacters(overlay, table));

        models.AddRange(ItemTable.Read(overlay));

        var listed = new HashSet<int>();
        var live = new HashSet<int>();
        int read = 0, unreadable = 0;

        foreach (var model in models)
        {
            var ids = model.Textures.TextureIds;
            var alternates = model.Textures.AlternateTextureIds;

            foreach (int id in ids.Concat(alternates)) listed.Add(id);

            var bytes = meshBytes(model.MeshAssetId);

            if (bytes is null || !MeshFile.TryParse(bytes, out var mesh))
            {
                // Unreadable: assume it still needs everything rather than free something it draws.
                unreadable++;
                foreach (int id in ids.Concat(alternates)) live.Add(id);
                continue;
            }

            read++;

            foreach (var part in mesh.Parts)
                foreach (var sub in part.SubMeshes)
                {
                    if (!sub.HasTexture) continue;

                    int slot = sub.TextureIndex;

                    // A slot keeps both the texture and its alternate alive: the game swaps between
                    // the two sets, so the pair stands or falls together.
                    if (slot >= 0 && slot < ids.Count) live.Add(ids[slot]);
                    if (slot >= 0 && slot < alternates.Count) live.Add(alternates[slot]);
                }
        }

        return new TextureUsageReport(
            listed.OrderBy(i => i).ToList(),
            live.OrderBy(i => i).ToList(),
            listed.Except(live).OrderBy(i => i).ToList(),
            read,
            unreadable);
    }

    /// <summary>
    /// A texture the same shape as the original but a single transparent colour, which is what a dead
    /// texture should cost.
    /// </summary>
    public static byte[] Blank(TextureFile texture)
    {
        var rgba = new byte[texture.Width * texture.Height * 4];   // all zero: transparent black
        var (data, _) = TextureWriter.Replace(texture, rgba, texture.Width, texture.Height);
        return data;
    }
}
