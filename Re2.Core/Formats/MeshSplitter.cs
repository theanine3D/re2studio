using System;
using System.Collections.Generic;

namespace Re2.Core.Formats;

/// <summary>
/// Cuts an arbitrary triangle soup into sub-meshes that fit the F3DEX2 vertex buffer.
/// </summary>
public static class MeshSplitter
{
    /// <summary>
    /// Splits <paramref name="triangles"/> (indices into <paramref name="pool"/>) into sub-meshes,
    /// each carrying its own compacted copy of just the vertices it uses.
    /// </summary>
    public static List<MeshBuildSubMesh> Split(
        IReadOnlyList<MeshVertex> pool,
        IReadOnlyList<MeshTriangle> triangles,
        int textureIndex = -1,
        uint colour0 = 0xB2B2B2FF,
        uint colour1 = 0x7F7F7FFF,
        int budget = MeshWriter.MaxVerticesPerLoad)
    {
        if (budget < 3) throw new ArgumentOutOfRangeException(nameof(budget), "A block must hold a triangle.");

        var result = new List<MeshBuildSubMesh>();
        var current = NewBlock(textureIndex, colour0, colour1);
        var localOf = new Dictionary<int, int>(budget);
        Span<int> corners = stackalloc int[3];
        Span<int> local = stackalloc int[3];

        foreach (var triangle in triangles)
        {
            corners[0] = triangle.A; corners[1] = triangle.B; corners[2] = triangle.C;

            // Degenerate triangles are kept: the retail data contains a few, and silently dropping them
            // would make a re-encode lose geometry the game shipped.
            if (corners[0] < 0 || corners[0] >= pool.Count ||
                corners[1] < 0 || corners[1] >= pool.Count ||
                corners[2] < 0 || corners[2] >= pool.Count) continue;

            int needed = 0;
            for (int i = 0; i < 3; i++)
            {
                if (localOf.ContainsKey(corners[i])) continue;
                // Count a repeated new corner once.
                bool alreadyCounted = false;
                for (int j = 0; j < i; j++) if (corners[j] == corners[i]) alreadyCounted = true;
                if (!alreadyCounted) needed++;
            }

            if (localOf.Count + needed > budget)
            {
                result.Add(current);
                current = NewBlock(textureIndex, colour0, colour1);
                localOf.Clear();
            }

            for (int i = 0; i < 3; i++)
            {
                if (!localOf.TryGetValue(corners[i], out int index))
                {
                    index = current.Vertices.Count;
                    current.Vertices.Add(pool[corners[i]]);
                    localOf[corners[i]] = index;
                }
                local[i] = index;
            }

            current.Triangles.Add(new MeshTriangle(local[0], local[1], local[2]));
        }

        if (current.Triangles.Count > 0) result.Add(current);
        return result;
    }

    private static MeshBuildSubMesh NewBlock(int textureIndex, uint colour0, uint colour1)
        => new() { TextureIndex = textureIndex, Colour0 = colour0, Colour1 = colour1 };
}
