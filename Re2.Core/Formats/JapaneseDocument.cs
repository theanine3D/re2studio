using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Re2.Core.Formats;

/// <summary>
/// A page of a document in Biohazard 2 (Japan). The Japanese files and memos are not text: each page
/// is a pre-rendered 256-pixel-wide picture in four grey levels, run-length coded. Ported from the
/// game's decoder at 0x80090F4C, which writes the pixels out as 4-bit texels for the viewer.
/// </summary>
public sealed class JapaneseDocument
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>One byte per pixel, each 0-3: 0 is the page's background, 3 the brightest ink.</summary>
    public byte[] Pixels { get; }

    public const int Levels = 4;

    public JapaneseDocument(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height)
            throw new ArgumentException($"{pixels.Length} pixels for a {width}x{height} page.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    // Run lengths: 0 -> 1, 10 -> 2, 110 + 3 bits -> 3..10, 1110 + 8 bits -> 11..266,
    // 11110 + 15 bits -> 267..33034, 11111 -> end of page. Each run is followed by its 2-bit level.
    private const int Base3 = 3, Base8 = 11, Base15 = 267;
    // The field holds up to 0x7FFF, but the retail encoder splits long runs at 32,000 past the base.
    private const int MaxRun = Base15 + 32000;

    /// <summary>Expands a page, or returns false when the asset is not one.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out JapaneseDocument? page)
    {
        page = null;
        if (data.Length < 8) return false;

        int width = BinaryPrimitives.ReadUInt16BigEndian(data);
        int height = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (width is < 8 or > 1024 || height is < 8 or > 1024) return false;

        var pixels = new byte[width * height];
        var reader = new Reader(data[4..]);
        int at = 0;

        while (true)
        {
            int run;
            if (reader.Read(1) == 0) run = 1;
            else if (reader.Read(1) == 0) run = 2;
            else if (reader.Read(1) == 0) run = (int)reader.Read(3) + Base3;
            else if (reader.Read(1) == 0) run = (int)reader.Read(8) + Base8;
            else if (reader.Read(1) == 0) run = (int)reader.Read(15) + Base15;
            else break;

            byte level = (byte)reader.Read(2);
            if (at + run > pixels.Length) return false;
            pixels.AsSpan(at, run).Fill(level);
            at += run;

            if (reader.PastEnd) return false;
        }

        if (at != pixels.Length) return false;

        page = new JapaneseDocument(width, height, pixels);
        return true;
    }

    /// <summary>Codes a page the way the retail assets are coded: each run as long as it can be.</summary>
    public byte[] Encode()
    {
        var writer = new Writer();

        for (int at = 0; at < Pixels.Length;)
        {
            byte level = Pixels[at];
            int run = 1;
            while (at + run < Pixels.Length && Pixels[at + run] == level && run < MaxRun) run++;

            if (run == 1) writer.Write(0, 1);
            else if (run == 2) writer.Write(0b01, 2);
            else if (run < Base8) { writer.Write(0b011, 3); writer.Write(run - Base3, 3); }
            else if (run < Base15) { writer.Write(0b0111, 4); writer.Write(run - Base8, 8); }
            else { writer.Write(0b01111, 5); writer.Write(run - Base15, 15); }

            writer.Write(level & 3, 2);
            at += run;
        }

        // Five set bits end the page. The retail encoder writes sixteen, but stops at the end of the
        // 32-bit unit it is in -- unless that unit has no more than the five the decoder needs.
        int room = 32 - writer.Bits % 32;
        int ones = room <= 5 ? 16 : Math.Min(16, room);
        writer.Write((1 << ones) - 1, ones);

        var body = writer.ToAlignedArray();
        var output = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(output, (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), (ushort)Height);
        body.CopyTo(output, 4);
        return output;
    }

    /// <summary>
    /// The game's bit reader: a 32-bit window, least significant bit first, refilled a big-endian
    /// halfword at a time.
    /// </summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _source;
        private int _position;
        private uint _window;
        private int _available;

        public Reader(ReadOnlySpan<byte> source)
        {
            _source = source;
            _window = (uint)(Half(source, 2) << 16) | Half(source, 0);
            _available = 32;
            _position = 4;
        }

        public bool PastEnd => _position > _source.Length + 8;

        private static uint Half(ReadOnlySpan<byte> s, int at)
            => at + 2 <= s.Length ? BinaryPrimitives.ReadUInt16BigEndian(s.Slice(at, 2)) : 0u;

        public uint Read(int bits)
        {
            uint value = _window & ((1u << bits) - 1);
            _window >>= bits;
            _available -= bits;

            if (_available < 16)
            {
                _window |= Half(_source, _position) << _available;
                _position += 2;
                _available += 16;
            }

            return value;
        }
    }

    /// <summary>The reader's mirror: bits least significant first, into big-endian halfwords.</summary>
    private sealed class Writer
    {
        private readonly List<byte> _bytes = new();
        private uint _word;
        private int _held;

        public void Write(int value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _word |= (uint)((value >> i) & 1) << _held;
                if (++_held == 16) Flush();
            }
        }

        private void Flush()
        {
            _bytes.Add((byte)(_word >> 8));
            _bytes.Add((byte)_word);
            _word = 0;
            _held = 0;
        }

        /// <summary>Total bits written so far.</summary>
        public int Bits => _bytes.Count * 8 + _held;

        /// <summary>The stream so far, padded with zeros to a whole 32-bit unit as the retail files are.</summary>
        public byte[] ToAlignedArray()
        {
            int keep = (Bits + 31) / 32 * 32;
            if (_held > 0) Flush();
            while (_bytes.Count * 8 < keep) _bytes.Add(0);
            return _bytes.ToArray();
        }
    }

    /// <summary>The page as 8-bit greys, for export: level n becomes n * 85.</summary>
    public byte[] ToGrey()
    {
        var grey = new byte[Pixels.Length];
        for (int i = 0; i < grey.Length; i++) grey[i] = (byte)(Pixels[i] * 85);
        return grey;
    }

    /// <summary>A page from 8-bit greys, each rounded to the nearest of the four levels.</summary>
    public static JapaneseDocument FromGrey(int width, int height, ReadOnlySpan<byte> grey)
    {
        var pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)Math.Clamp((grey[i] + 42) / 85, 0, 3);
        return new JapaneseDocument(width, height, pixels);
    }
}
