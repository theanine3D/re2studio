using System;
using System.Collections.Generic;
using Re2.Core.Rom;

namespace Re2.Core.Assets;

/// <summary>
/// The small item pictures on the inventory screen -- the handgun, the spray, the herbs.
/// </summary>
public static class InventoryIcons
{
    public const int Width = 40, Height = 30, Size = Width * Height;

    /// <summary>Rev 1 asset ids; other builds number them differently (see <see cref="Re2Layout"/>).</summary>
    public const int FirstAsset = 5336;
    public const int Count = 106;

    public const int BundleAsset = 5334;
    public const int BundleCount = 14;

    /// <summary>The inventory screen, whose palette the icons are drawn with.</summary>
    public const int PaletteAsset = 5329;
    public const int PaletteIndex = 1;

    /// <summary>One icon: the asset it lives in and where in that asset it starts.</summary>
    public sealed record Icon(int Number, int AssetId, int Offset, int ItemId)
    {
        public bool InBundle => Number >= Count;

        public override string ToString()
            => InBundle ? $"extra {Number - Count}" : $"icon {ItemId}";
    }

    /// <summary>Every icon in Rev 1: the 106 by item, then the fourteen extras.</summary>
    public static IReadOnlyList<Icon> All { get; } = Build(Re2Layout.UsaRev1);

    private static readonly IReadOnlyList<Icon> EuropeIcons = Build(Re2Layout.Europe);

    /// <summary>Every icon, with the asset ids of the given build.</summary>
    public static IReadOnlyList<Icon> For(Re2Layout layout)
        => layout == Re2Layout.Europe ? EuropeIcons
         : layout.IconFirstAsset == FirstAsset ? All : Build(layout);

    private static List<Icon> Build(Re2Layout layout)
    {
        var icons = new List<Icon>(Count + BundleCount);

        for (int i = 0; i < Count; i++) icons.Add(new Icon(i, layout.IconFirstAsset + i, 0, i));
        for (int i = 0; i < BundleCount; i++) icons.Add(new Icon(Count + i, layout.IconBundleAsset, i * Size, -1));

        return icons;
    }

    /// <summary>Is this asset one the icons live in?</summary>
    public static bool IsIconAsset(int assetId, Re2Layout layout)
        => assetId == layout.IconBundleAsset ||
           (assetId >= layout.IconFirstAsset && assetId < layout.IconFirstAsset + Count);

    /// <summary>
    /// Checks that an asset's bytes are the size the icon needs, so a wrong or damaged asset is
    /// reported rather than drawn as noise.
    /// </summary>
    public static bool Fits(Icon icon, ReadOnlySpan<byte> asset)
        => asset.Length >= icon.Offset + Size;

    /// <summary>One icon's pixels as RGBA, through a palette given as RGBA quads.</summary>
    public static byte[] ToRgba(ReadOnlySpan<byte> asset, Icon icon, ReadOnlySpan<byte> paletteRgba)
    {
        var rgba = new byte[Size * 4];
        var pixels = asset.Slice(icon.Offset, Size);

        for (int i = 0; i < Size; i++)
        {
            int c = pixels[i] * 4;
            if (c + 3 >= paletteRgba.Length) continue;          // left transparent: not a colour we have

            rgba[i * 4] = paletteRgba[c];
            rgba[i * 4 + 1] = paletteRgba[c + 1];
            rgba[i * 4 + 2] = paletteRgba[c + 2];

            // The icons are drawn opaque on their navy panel.
            rgba[i * 4 + 3] = 255;
        }

        return rgba;
    }

    /// <summary>Matches a 40x30 RGBA picture into the palette, returning the new icon pixels.</summary>
    public static byte[] Match(ReadOnlySpan<byte> rgba, ReadOnlySpan<byte> original,
                               ReadOnlySpan<byte> paletteRgba)
    {
        if (rgba.Length != Size * 4)
            throw new ArgumentException($"An icon is {Width}x{Height}.", nameof(rgba));

        int colours = paletteRgba.Length / 4;
        var pixels = new byte[Size];
        var cache = new Dictionary<int, byte>();

        for (int i = 0; i < Size; i++)
        {
            int r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];

            int was = original.Length == Size ? original[i] : -1;
            if (was >= 0 && was < colours &&
                paletteRgba[was * 4] == r && paletteRgba[was * 4 + 1] == g && paletteRgba[was * 4 + 2] == b)
            {
                pixels[i] = (byte)was;
                continue;
            }

            int key = (r << 16) | (g << 8) | b;
            if (!cache.TryGetValue(key, out byte best))
            {
                int bestDistance = int.MaxValue;
                for (int c = 0; c < colours; c++)
                {
                    int dr = paletteRgba[c * 4] - r, dg = paletteRgba[c * 4 + 1] - g, db = paletteRgba[c * 4 + 2] - b;
                    int distance = dr * dr + dg * dg + db * db;
                    if (distance < bestDistance) { bestDistance = distance; best = (byte)c; }
                    if (distance == 0) break;
                }

                cache[key] = best;
            }

            pixels[i] = best;
        }

        return pixels;
    }

    /// <summary>An asset with one icon's pixels replaced, the rest of it left as it was.</summary>
    public static byte[] Replace(ReadOnlySpan<byte> asset, Icon icon, ReadOnlySpan<byte> pixels)
    {
        var result = asset.ToArray();
        pixels.CopyTo(result.AsSpan(icon.Offset, Size));
        return result;
    }
}
