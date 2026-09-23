using System.Linq;
using Re2.Core.Assets;
using Xunit;

namespace Re2.Tests;

public class SoundDirectoryTests
{
    private static SoundDirectory Read()
    {
        var rom = TestRom.Rom;
        return SoundDirectory.Read(rom, AssetDirectory.Read(rom));
    }

    [RomFact]
    public void ReadsEverySample()
    {
        var sound = Read();
        Assert.Equal(1192, sound.Samples.Count);
        Assert.Equal(9_022_912, sound.SampleData.Length);
    }

    /// <summary>
    /// The decisive structural check: the directory must tile the sample blob exactly.
    /// </summary>
    [RomFact]
    public void SamplesTileTheBlobExactly()
    {
        var sound = Read();
        Assert.Equal(sound.SampleData.Length, sound.Samples.Sum(s => (long)s.StoredSize));
    }

    [RomFact]
    public void OffsetsAscendAndStayInsideTheBlob()
    {
        var sound = Read();
        for (int i = 0; i < sound.Samples.Count; i++)
        {
            var s = sound.Samples[i];
            Assert.InRange(s.Offset, 0, sound.SampleData.Length - 1);
            Assert.True(s.StoredSize > 0, $"sample {s.Index} is empty");
            if (i > 0) Assert.True(sound.Samples[i - 1].Offset < s.Offset, "offsets are not ascending");
        }
    }

    /// <summary>Index fields run 1..n, which is what confirms the field is an id and not padding.</summary>
    [RomFact]
    public void IndicesAreSequential()
    {
        var sound = Read();
        for (int i = 0; i < sound.Samples.Count; i++)
            Assert.Equal(i + 1, sound.Samples[i].Index);
    }

    /// <summary>Every rate must be a believable audio rate, which is how the field was identified.</summary>
    [RomFact]
    public void SampleRatesArePlausible()
    {
        var sound = Read();
        foreach (var s in sound.Samples)
            Assert.InRange(s.SampleRate, 4000, 48000);

        // The common rates should dominate.
        var top = sound.Samples.GroupBy(s => s.SampleRate).OrderByDescending(g => g.Count()).First();
        Assert.Equal(16000, top.Key);
        Assert.True(top.Count() > 500);
    }

    /// <summary>
    /// Records the fact that the data is compressed: far below 16 bits per sample, and not the 4 of
    /// plain ADPCM either. If this ever reads 16, the codec assumption needs revisiting.
    /// </summary>
    [RomFact]
    public void SampleDataIsCompressed()
    {
        var sound = Read();
        var bits = sound.Samples.Where(s => s.DeclaredLength > 0).Select(s => s.BitsPerSample).OrderBy(x => x).ToList();
        double median = bits[bits.Count / 2];

        Assert.InRange(median, 4.5, 8.0);
    }

    [RomFact]
    public void EncodedDataIsRetrievableForEverySample()
    {
        var sound = Read();
        foreach (var s in sound.Samples)
            Assert.Equal(s.StoredSize, sound.GetEncoded(s).Length);
    }
}
