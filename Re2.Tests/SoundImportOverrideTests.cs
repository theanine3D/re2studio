using System;
using System.IO;
using System.Linq;
using Re2.Core.Assets;
using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>
/// The editor's own import path, over a project that already holds an edit for the same sample.
/// </summary>
public sealed class SoundImportOverrideTests
{
    /// <summary>A mono 16-bit WAV of <paramref name="seconds"/> at <paramref name="rate"/>.</summary>
    private static byte[] Wav(int rate, double seconds)
    {
        int count = (int)(rate * seconds);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write("RIFF".ToCharArray()); w.Write(36 + count * 2); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(count * 2);
        for (int i = 0; i < count; i++) w.Write((short)(Math.Sin(i * 0.05) * 12000));

        w.Flush();
        return ms.ToArray();
    }

    [RomFact]
    public void ASecondImportOverAnEditedSampleStillLandsOnTheCartsRate()
    {
        string folder = Path.Combine(Path.GetTempPath(), "re2-sound-" + Guid.NewGuid().ToString("N")[..8]);
        string wav = Path.Combine(Path.GetTempPath(), "re2-tone-" + Guid.NewGuid().ToString("N")[..8] + ".wav");

        try
        {
            Re2.Core.Project.ProjectFolder.Extract(TestRom.Rom, folder);
            File.WriteAllBytes(wav, Wav(44100, 1.0));

            using var session = new RomSession(TestRom.Path!);
            int index = session.Sounds.Samples.First(s => s.SampleRate == 16000 && s.DeclaredLength > 1000).Index;

            // First import: resampled to the cart's rate.
            AssetIo.ImportSound(session, folder, index, wav);

            session.Overrides.Scan(folder, session.Rom);
            session.Overrides.WaitForScan();
            session.InvalidateCaches();

            // Second import over the edit.
            string log = AssetIo.ImportSound(session, folder, index, wav);

            session.Overrides.Scan(folder, session.Rom);
            session.Overrides.WaitForScan();
            session.InvalidateCaches();

            var stored = session.Sounds.Samples.First(s => s.Index == index);

            Assert.Equal(16000, stored.SampleRate);
            Assert.Contains("resampled", log);
            Assert.Equal(16000, session.CartSounds.Samples.First(s => s.Index == index).SampleRate);
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { }
            try { if (File.Exists(wav)) File.Delete(wav); } catch (IOException) { }
        }
    }
}
