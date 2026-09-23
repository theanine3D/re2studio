using System;
using System.Collections.Generic;
using System.Linq;
using Re2.Core.Patch;
using Xunit;
using Xunit.Abstractions;

namespace Re2.Tests;

/// <summary>Applying patches made by other tools.</summary>
public class BpsApplyTests
{
    private readonly ITestOutputHelper _out;

    public BpsApplyTests(ITestOutputHelper output) => _out = output;

    private const int SourceRead = 0, TargetRead = 1, SourceCopy = 2, TargetCopy = 3;

    /// <summary>The format's variable-length number, written independently of the implementation.</summary>
    private static void Number(List<byte> to, ulong value)
    {
        while (true)
        {
            byte low = (byte)(value & 0x7F);
            value >>= 7;
            if (value == 0) { to.Add((byte)(0x80 | low)); return; }
            to.Add(low);
            value--;
        }
    }

    private static void Action(List<byte> to, int action, int length)
        => Number(to, ((ulong)(length - 1) << 2) | (uint)action);

    private static void Signed(List<byte> to, int offset)
        => Number(to, ((ulong)Math.Abs(offset) << 1) | (offset < 0 ? 1u : 0u));

    private static byte[] Finish(List<byte> patch, byte[] source, byte[] target)
    {
        void Checksum(uint value)
        {
            patch.Add((byte)value); patch.Add((byte)(value >> 8));
            patch.Add((byte)(value >> 16)); patch.Add((byte)(value >> 24));
        }

        Checksum(Crc32.Compute(source));
        Checksum(Crc32.Compute(target));
        Checksum(Crc32.Compute(patch.ToArray()));
        return patch.ToArray();
    }

    private static List<byte> Header(byte[] source, int targetSize)
    {
        var patch = new List<byte>(System.Text.Encoding.ASCII.GetBytes("BPS1"));
        Number(patch, (ulong)source.Length);
        Number(patch, (ulong)targetSize);
        Number(patch, 0);
        return patch;
    }

    /// <summary>All four actions in one patch, which is what a real differ produces.</summary>
    [Fact]
    public void AllFourActionsAreApplied()
    {
        var source = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

        var expected = new List<byte>();
        expected.AddRange(source.Take(4));                 // SourceRead 4
        expected.AddRange(new byte[] { 0xAA, 0xBB });      // TargetRead 2
        expected.AddRange(source.Skip(10).Take(3));        // SourceCopy from 10, length 3
        expected.AddRange(expected.Take(3).ToArray());     // TargetCopy from 0, length 3

        var patch = Header(source, expected.Count);
        Action(patch, SourceRead, 4);
        Action(patch, TargetRead, 2); patch.Add(0xAA); patch.Add(0xBB);
        Action(patch, SourceCopy, 3); Signed(patch, 10);   // the relative offset starts at zero
        Action(patch, TargetCopy, 3); Signed(patch, 0);

        var result = BpsPatch.Apply(Finish(patch, source, expected.ToArray()), source);

        Assert.Equal(expected, result.Target);
        _out.WriteLine(string.Join(" ", result.Target.Select(b => b.ToString("X2"))));
    }

    /// <summary>
    /// A copy offset is relative to where the last copy of that kind left off, and may be negative.
    /// </summary>
    [Fact]
    public void CopyOffsetsAreRelativeAndMayBeNegative()
    {
        var source = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var expected = new List<byte>();
        expected.AddRange(source.Skip(20).Take(4));        // first copy: +20
        expected.AddRange(source.Skip(8).Take(4));         // then back 16 from where it stopped, at 24

        var patch = Header(source, expected.Count);
        Action(patch, SourceCopy, 4); Signed(patch, 20);
        Action(patch, SourceCopy, 4); Signed(patch, -16);

        var result = BpsPatch.Apply(Finish(patch, source, expected.ToArray()), source);
        Assert.Equal(expected, result.Target);
    }

    /// <summary>TargetCopy can read what this same patch has just written, which is how it makes runs.</summary>
    [Fact]
    public void TargetCopyCanOverlapWhatItIsWriting()
    {
        var source = new byte[] { 1, 2, 3, 4 };
        var expected = new byte[] { 0x7F, 0x7F, 0x7F, 0x7F, 0x7F };

        var patch = Header(source, expected.Length);
        Action(patch, TargetRead, 1); patch.Add(0x7F);
        Action(patch, TargetCopy, 4); Signed(patch, 0);

        var result = BpsPatch.Apply(Finish(patch, source, expected), source);
        Assert.Equal(expected, result.Target);
    }

    [Fact]
    public void MetadataComesBack()
    {
        var source = new byte[] { 1, 2, 3, 4 };
        var target = new byte[] { 1, 2, 3, 4 };

        var patch = new List<byte>(System.Text.Encoding.ASCII.GetBytes("BPS1"));
        Number(patch, 4);
        Number(patch, 4);

        var metadata = System.Text.Encoding.UTF8.GetBytes("made by someone else");
        Number(patch, (ulong)metadata.Length);
        patch.AddRange(metadata);
        Action(patch, SourceRead, 4);

        var result = BpsPatch.Apply(Finish(patch, source, target), source);
        Assert.Equal("made by someone else", result.Metadata);
    }

    // ---- files that are not what they claim to be -------------------------

    [Fact]
    public void ANonPatchIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => BpsPatch.Apply(new byte[32], new byte[4]));

        Assert.Contains("not a BPS patch", error.Message);
    }

    [Fact]
    public void ATruncatedPatchIsRejectedRatherThanCrashing()
    {
        var source = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var target = source.ToArray();
        target[5] ^= 0xFF;

        var whole = BpsPatch.Create(source, target);

        for (int cut = 5; cut < whole.Length; cut += 3)
        {
            var truncated = whole.Take(cut).ToArray();
            Assert.ThrowsAny<InvalidOperationException>(() => BpsPatch.Apply(truncated, source));
        }
    }

    /// <summary>A flipped bit anywhere in the patch is caught by its own checksum.</summary>
    [Fact]
    public void ADamagedPatchIsCaught()
    {
        var source = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
        var target = source.ToArray();
        target[100] ^= 0xFF;

        var whole = BpsPatch.Create(source, target);

        for (int at = 4; at < whole.Length - 4; at += 7)
        {
            var damaged = whole.ToArray();
            damaged[at] ^= 0x01;

            var error = Assert.ThrowsAny<InvalidOperationException>(() => BpsPatch.Apply(damaged, source));
            Assert.Contains("damaged", error.Message);
        }
    }

    /// <summary>A patch that writes past the end of its declared ROM must not run off the array.</summary>
    [Fact]
    public void APatchThatOverrunsItsOutputIsRejected()
    {
        var source = new byte[] { 1, 2, 3, 4 };

        var patch = Header(source, 4);
        Action(patch, TargetRead, 64);                     // far more than the four bytes declared
        for (int i = 0; i < 64; i++) patch.Add(0xEE);

        var error = Assert.ThrowsAny<InvalidOperationException>(
            () => BpsPatch.Apply(Finish(patch, source, new byte[4]), source));

        Assert.Contains("damaged", error.Message);
    }
}
