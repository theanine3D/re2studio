using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Re2.Core.Formats;

/// <summary>
/// One foreground piece: a rectangle of the screen that draws in front of the player, at a depth.
/// </summary>
public sealed record MaskPiece(int X, int Y, int Width, int Height, int Depth, bool HasOwnPixels)
{
    /// <summary>
    /// Which pixels of the rectangle actually draw, row-major, <see cref="Width"/> by <see
    /// cref="Height"/>.
    /// </summary>
    public bool[] Covered { get; init; } = Array.Empty<bool>();

    /// <summary>Palette indices for a piece that carries its own image, row-major.</summary>
    public byte[] Pixels { get; init; } = Array.Empty<byte>();

    /// <summary>The piece header's flag word exactly as stored.</summary>
    public int Flags { get; init; }

    /// <summary>The byte of padding after this piece, when its data ended on an odd boundary.</summary>
    public byte Padding { get; init; }

    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// The foreground layer for one camera angle: the parts of the scene that draw in front of the player
/// rather than behind.
/// </summary>
public sealed class MaskFile
{
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<MaskPiece> Pieces { get; }

    /// <summary>The 256-entry palette, as stored. Empty when the file carries none.</summary>
    public IReadOnlyList<ushort> Palette { get; }

    /// <summary>Whatever follows the palette, kept verbatim.</summary>
    public byte[] Trailing { get; }

    private MaskFile(int width, int height, IReadOnlyList<MaskPiece> pieces,
                     IReadOnlyList<ushort> palette, byte[] trailing)
    {
        Width = width;
        Height = height;
        Pieces = pieces;
        Palette = palette;
        Trailing = trailing;
    }

    public const int HeaderSize = 6;
    public const int PaletteEntries = 256;

    /// <summary>Marks a run as skipped rather than covered.</summary>
    private const byte SkipRun = 0x80;

    private const int OwnPixelsFlag = 0x8000;

    public static bool TryParse(ReadOnlySpan<byte> data, out MaskFile? mask)
    {
        mask = null;
        if (data.Length < HeaderSize) return false;

        int width = BinaryPrimitives.ReadUInt16BigEndian(data);
        int height = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);

        // The dimensions are the screen's, and are the cheapest way to tell a mask from anything else.
        if (width is not (> 0 and <= 1024) || height is not (> 0 and <= 1024)) return false;
        if (count is < 0 or > 4096) return false;

        var pieces = new List<MaskPiece>(count);
        int at = HeaderSize;

        for (int i = 0; i < count; i++)
        {
            if (at + 8 > data.Length) return false;

            int flags = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            int x = data[at + 2];
            int y = data[at + 3];
            int packed = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 4)..]);
            int pieceWidth = data[at + 6];
            int pieceHeight = data[at + 7];
            at += 8;

            x |= (packed & 0xC000) >> 6;
            y |= (packed & 0x2000) >> 5;

            int depth = packed & 0x1FFF;
            bool ownPixels = (flags & OwnPixelsFlag) != 0;

            var covered = new bool[pieceWidth * pieceHeight];
            byte[] pixels = Array.Empty<byte>();

            if (ownPixels)
            {
                if (at + pieceWidth * pieceHeight > data.Length) return false;

                pixels = data.Slice(at, pieceWidth * pieceHeight).ToArray();
                at += pieceWidth * pieceHeight;
                Array.Fill(covered, true);
            }
            else if (!ReadCoverage(data, ref at, pieceWidth, pieceHeight, covered))
            {
                return false;
            }

            // Pieces start on an even boundary; an odd-length one is padded.
            byte padding = 0;
            if ((at & 1) != 0)
            {
                padding = at < data.Length ? data[at] : (byte)0;
                at++;
            }

            pieces.Add(new MaskPiece(x, y, pieceWidth, pieceHeight, depth, ownPixels)
            {
                Covered = covered,
                Pixels = pixels,
                Flags = flags,
                Padding = padding
            });
        }

        var palette = new List<ushort>();
        if (at + PaletteEntries * 2 <= data.Length)
        {
            for (int i = 0; i < PaletteEntries; i++)
                palette.Add(BinaryPrimitives.ReadUInt16BigEndian(data[(at + i * 2)..]));

            at += PaletteEntries * 2;
        }

        mask = new MaskFile(width, height, pieces, palette, data[at..].ToArray());
        return true;
    }

    /// <summary>Walks one piece's runs.</summary>
    private static bool ReadCoverage(ReadOnlySpan<byte> data, ref int at, int width, int height, bool[] covered)
    {
        for (int row = 0; row < height; row++)
        {
            int column = 0;

            while (column < width)
            {
                if (at >= data.Length) return false;

                byte run = data[at++];
                int length = run & 0x7F;

                // A zero-length run would never finish the row.
                if (length == 0 || column + length > width) return false;

                if ((run & SkipRun) == 0)
                    for (int i = 0; i < length; i++) covered[row * width + column + i] = true;

                column += length;
            }
        }

        return true;
    }

    /// <summary>Writes the mask back out in the game's own format.</summary>
    public byte[] Write()
    {
        var output = new List<byte>(1 << 14);

        void U16(int value)
        {
            output.Add((byte)(value >> 8));
            output.Add((byte)value);
        }

        U16(Width);
        U16(Height);
        U16(Pieces.Count);

        foreach (var piece in Pieces)
        {
            if (piece.X is < 0 or > 1023 || piece.Y is < 0 or > 511)
                throw new InvalidDataException($"A piece at {piece.X},{piece.Y} is off the screen the format can address.");
            // Zero is allowed, and the retail data uses it: an empty piece encodes nothing at all.
            if (piece.Width is < 0 or > 255 || piece.Height is < 0 or > 255)
                throw new InvalidDataException($"A piece of {piece.Width}x{piece.Height} does not fit the format's 255 limit.");
            if (piece.Depth is < 0 or > 0x1FFF)
                throw new InvalidDataException($"A depth of {piece.Depth} does not fit the format's 13 bits.");

            int packed = piece.Depth
                       | ((piece.X & 0x300) << 6)
                       | ((piece.Y & 0x100) << 5);

            U16(piece.Flags);
            output.Add((byte)(piece.X & 0xFF));
            output.Add((byte)(piece.Y & 0xFF));
            U16(packed);
            output.Add((byte)piece.Width);
            output.Add((byte)piece.Height);

            if (piece.HasOwnPixels) output.AddRange(piece.Pixels);
            else WriteCoverage(output, piece);

            // Pieces start on an even boundary, as when read.
            if ((output.Count & 1) != 0) output.Add(piece.Padding);
        }

        foreach (ushort colour in Palette) U16(colour);
        output.AddRange(Trailing);

        return output.ToArray();
    }

    /// <summary>Writes one piece's rows as runs.</summary>
    private static void WriteCoverage(List<byte> output, MaskPiece piece)
    {
        for (int row = 0; row < piece.Height; row++)
        {
            int column = 0;

            while (column < piece.Width)
            {
                bool covered = piece.Covered[row * piece.Width + column];

                int length = 0;
                while (column + length < piece.Width
                       && piece.Covered[row * piece.Width + column + length] == covered
                       && length < MaxRun)
                {
                    length++;
                }

                output.Add((byte)(covered ? length : length | SkipRun));
                column += length;
            }
        }
    }

    /// <summary>The most pixels one run can carry: the length is seven bits, and zero would not advance.</summary>
    public const int MaxRun = 0x7F;

    /// <summary>
    /// The same mask with its coverage taken from a full-screen map -- what an imported image means.
    /// </summary>
    public MaskFile WithCoverage(ReadOnlySpan<bool> screen, out int outside, out int untouched)
    {
        var covers = new bool[Width * Height];
        untouched = 0;

        var pieces = new List<MaskPiece>(Pieces.Count);

        foreach (var piece in Pieces)
        {
            if (piece.HasOwnPixels)
            {
                untouched++;
                pieces.Add(piece);
                MarkCovered(covers, piece);
                continue;
            }

            var covered = new bool[piece.Width * piece.Height];

            for (int row = 0; row < piece.Height; row++)
            {
                int y = piece.Y + row;
                if (y < 0 || y >= Height) continue;

                for (int column = 0; column < piece.Width; column++)
                {
                    int x = piece.X + column;
                    if (x < 0 || x >= Width) continue;

                    if (screen[y * Width + x]) covered[row * piece.Width + column] = true;
                }
            }

            var replaced = piece with { Covered = covered };
            pieces.Add(replaced);
            MarkCovered(covers, replaced);
        }

        // Anything asked for where no piece can put it.
        outside = 0;
        for (int i = 0; i < covers.Length; i++)
            if (screen[i] && !covers[i]) outside++;

        return new MaskFile(Width, Height, pieces, Palette, Trailing);
    }

    private void MarkCovered(bool[] screen, MaskPiece piece)
    {
        for (int row = 0; row < piece.Height; row++)
        {
            int y = piece.Y + row;
            if (y < 0 || y >= Height) continue;

            for (int column = 0; column < piece.Width; column++)
            {
                int x = piece.X + column;
                if (x < 0 || x >= Width) continue;

                if (piece.Covered[row * piece.Width + column]) screen[y * Width + x] = true;
            }
        }
    }

    /// <summary>Pixels this mask puts in front of the player, over the whole screen.</summary>
    public int CoveredPixels
    {
        get
        {
            int total = 0;
            foreach (var piece in Pieces)
                foreach (bool covered in piece.Covered)
                    if (covered) total++;

            return total;
        }
    }
}
