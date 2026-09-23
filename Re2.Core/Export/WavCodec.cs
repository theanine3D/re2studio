using System;
using System.Buffers.Binary;
using System.IO;

namespace Re2.Core.Export;

/// <summary>Minimal 16-bit PCM WAV reader/writer -- enough to move samples in and out of the bank.</summary>
public static class WavCodec
{
    public const int HeaderBytes = 44;

    public static byte[] Write(ReadOnlySpan<short> pcm, int sampleRate, int channels = 1)
    {
        int dataBytes = pcm.Length * 2;
        var buffer = new byte[HeaderBytes + dataBytes];
        var s = buffer.AsSpan();

        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 1);                       // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(s[32..], (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], (uint)dataBytes);

        for (int i = 0; i < pcm.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(HeaderBytes + i * 2, 2), pcm[i]);

        return buffer;
    }

    /// <summary>Reads 16-bit PCM. Multi-channel input is mixed down, since the bank is mono.</summary>
    public static (short[] Pcm, int SampleRate) Read(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int rate = 0, channels = 1;
        int pos = 12;

        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4));
            var body = wav.Slice(pos + 8, Math.Min(size, wav.Length - pos - 8));

            if (id.SequenceEqual("fmt "u8) && body.Length >= 16)
            {
                int format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(body[2..]));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                int bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format != 1 || bits != 16)
                    throw new InvalidDataException($"Only 16-bit PCM WAV is supported (got format {format}, {bits}-bit).");
            }
            else if (id.SequenceEqual("data"u8))
            {
                int frames = body.Length / 2 / channels;
                var pcm = new short[frames];
                for (int i = 0; i < frames; i++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += BinaryPrimitives.ReadInt16LittleEndian(body.Slice((i * channels + c) * 2, 2));
                    pcm[i] = (short)(sum / channels);
                }
                return (pcm, rate == 0 ? 22050 : rate);
            }

            pos += 8 + size + (size & 1);
        }

        throw new InvalidDataException("WAV has no data chunk.");
    }
}
