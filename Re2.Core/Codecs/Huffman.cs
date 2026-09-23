using System;
using System.Collections.Generic;

namespace Re2.Core.Codecs;

/// <summary>Canonical Huffman code construction with a hard ceiling on code length.</summary>
public static class Huffman
{
    /// <summary>
    /// Code lengths for each symbol, zero where the symbol is unused, none longer than
    /// <paramref name="limit"/>.
    /// </summary>
    public static int[] Lengths(long[] frequencies, int limit)
    {
        var lengths = new int[frequencies.Length];

        // Only used symbols take part, in increasing frequency: package-merge consumes them in
        // that order and the reconstruction below relies on it.
        var used = new List<int>();
        for (int i = 0; i < frequencies.Length; i++) if (frequencies[i] > 0) used.Add(i);
        used.Sort((a, b) => frequencies[a] != frequencies[b]
            ? frequencies[a].CompareTo(frequencies[b])
            : a.CompareTo(b));

        // A code needs at least two symbols to distinguish anything, so a single used symbol still
        // gets one bit and an empty alphabet gets nothing.
        if (used.Count == 0) return lengths;
        if (used.Count == 1) { lengths[used[0]] = 1; return lengths; }

        // Each level is the previous level's items paired up, merged back with the singletons.
        var levels = new List<(long Weight, bool Single)[]>();
        (long Weight, bool Single)[] previous = Array.Empty<(long, bool)>();

        for (int level = 0; level < limit; level++)
        {
            var merged = new List<(long Weight, bool Single)>(used.Count + previous.Length / 2);

            int s = 0, p = 0;
            while (s < used.Count || p + 1 < previous.Length)
            {
                bool takeSingle = p + 1 >= previous.Length
                    || (s < used.Count
                        && frequencies[used[s]] <= previous[p].Weight + previous[p + 1].Weight);

                if (takeSingle) merged.Add((frequencies[used[s++]], true));
                else { merged.Add((previous[p].Weight + previous[p + 1].Weight, false)); p += 2; }
            }

            previous = merged.ToArray();
            levels.Add(previous);
        }

        // Walk back down.
        int take = 2 * used.Count - 2;

        for (int level = limit - 1; level >= 0 && take > 0; level--)
        {
            var items = levels[level];
            int packages = 0;

            for (int i = 0; i < take && i < items.Length; i++) if (!items[i].Single) packages++;

            int singles = Math.Min(take, items.Length) - packages;
            for (int i = 0; i < singles; i++) lengths[used[i]]++;

            take = 2 * packages;
        }

        return lengths;
    }

    /// <summary>
    /// The canonical codes for a set of lengths: symbols of equal length are numbered in symbol
    /// order, and each length starts where the previous one left off.
    /// </summary>
    public static int[] Codes(int[] lengths)
    {
        int longest = 0;
        foreach (int length in lengths) longest = Math.Max(longest, length);

        var counts = new int[longest + 1];
        foreach (int length in lengths) if (length > 0) counts[length]++;

        var next = new int[longest + 2];
        int code = 0;
        for (int length = 1; length <= longest; length++)
        {
            code = (code + counts[length - 1]) << 1;
            next[length] = code;
        }

        var codes = new int[lengths.Length];
        for (int i = 0; i < lengths.Length; i++)
            if (lengths[i] > 0) codes[i] = next[lengths[i]]++;

        return codes;
    }
}
