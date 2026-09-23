using System;
using System.IO;
using System.Text;
using Re2.Core.Project;
using Xunit;

namespace Re2.Tests;

/// <summary>Reading a project file must not stop anything else writing to it.</summary>
public sealed class SharedFileTests : IDisposable
{
    private readonly string _folder;
    private readonly string _path;

    public SharedFileTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "re2-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "02942_Stored.bin");
        File.WriteAllBytes(_path, new byte[] { 1, 2, 3, 4 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AReadInProgressDoesNotBlockAWrite()
    {
        using var reading = SharedFile.OpenRead(_path);

        // This is the write the extract was making when it failed.
        File.WriteAllBytes(_path, new byte[] { 9, 9 });

        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(_path));
    }

    /// <summary>
    /// The behaviour being corrected, stated so the fix cannot quietly be undone by going back to
    /// <see cref="File.ReadAllBytes"/>: the framework's own read really does deny the writer.
    /// </summary>
    [WindowsFact]
    public void TheFrameworkDefaultReadWouldHaveBlockedIt()
    {
        using var reading = File.OpenRead(_path);

        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(_path, new byte[] { 9, 9 }));
    }

    [Fact]
    public void SharedReadsReturnTheContent()
    {
        // Written without a byte order mark, so the bytes are exactly the text.
        File.WriteAllText(_path, "hello", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Equal("hello", SharedFile.ReadAllText(_path));
        Assert.Equal(Encoding.UTF8.GetBytes("hello"), SharedFile.ReadAllBytes(_path));
    }

    /// <summary>A byte order mark is consumed rather than returned, as the framework's read does.</summary>
    [Fact]
    public void SharedReadsSkipAByteOrderMark()
    {
        File.WriteAllText(_path, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("{}", SharedFile.ReadAllText(_path));
        Assert.Equal(File.ReadAllText(_path), SharedFile.ReadAllText(_path));
    }

    [Fact]
    public void ASharedReadSucceedsWhileAnotherReaderHoldsTheFile()
    {
        using var other = SharedFile.OpenRead(_path);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, SharedFile.ReadAllBytes(_path));
    }
}

/// <summary>A fact that only means something on Windows, where open files lock out writers.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows file locking; Linux does not deny writers.";
    }
}
