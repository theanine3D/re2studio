using System;
using System.IO;
using Re2.Core.Rom;
using Xunit;

namespace Re2.Tests;

/// <summary>Locates the retail ROM for tests that need it.</summary>
public static class TestRom
{
    private static readonly Lazy<string?> PathLazy = new(Locate);

    public static string? Path => PathLazy.Value;
    public static bool Available => Path is not null;

    private static readonly Lazy<RomFile?> RomLazy = new(() => Path is null ? null : RomFile.Load(Path));

    public static RomFile Rom => RomLazy.Value ?? throw new InvalidOperationException("ROM not available.");

    /// <summary>SHA-256 of Resident Evil 2 (U) [!].z64, the cart every expectation here is written against.</summary>
    private const string RetailSha256 =
        "71F3F779613BF1F0E2050BFA600425385D2C257A647D2E40F63BE1A7986E9AAC";

    private static bool IsRetail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)) == RetailSha256;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? Locate()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("RE2_ROM");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        // Walk up from the test binary looking for a sibling RE2 folder holding the cart image.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var candidate in new[]
                     {
                         System.IO.Path.Combine(dir.FullName, "RE2"),
                         System.IO.Path.Combine(dir.FullName, "..", "RE2")
                     })
            {
                if (!Directory.Exists(candidate)) continue;

                var roms = Directory.GetFiles(candidate, "*.z64");

                // The genuine cart first, wherever it sits in the listing.
                foreach (var file in roms)
                    if (IsRetail(file)) return file;

                // Otherwise fall back to whatever is there, in a stable order, so a folder with only
                // a differently-named copy still runs.
                Array.Sort(roms, StringComparer.OrdinalIgnoreCase);
                if (roms.Length > 0) return roms[0];
            }
            dir = dir.Parent;
        }
        return null;
    }
}

/// <summary>Marks a fact that requires the retail ROM; skipped rather than failed when it is missing.</summary>
public sealed class RomFactAttribute : FactAttribute
{
    public RomFactAttribute()
    {
        if (!TestRom.Available)
            Skip = "Retail RE2 ROM not found. Set RE2_ROM to its path to enable this test.";
    }
}

/// <summary>Both USA builds, for the tests that compare them.</summary>
public static class BothRoms
{
    public static string? Rev0 => Find("Resident Evil 2 (USA).z64");
    public static string? Rev1 => Find("Resident Evil 2 (USA) (Rev 1).z64");

    public static bool Available => Rev0 is not null && Rev1 is not null;

    private static string? Find(string name)
    {
        string? folder = TestRom.Path is null ? null : System.IO.Path.GetDirectoryName(TestRom.Path);
        if (folder is null) return null;

        string path = System.IO.Path.Combine(folder, name);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>Marks a fact needing both USA builds side by side.</summary>
public sealed class BothRomsFactAttribute : FactAttribute
{
    public BothRomsFactAttribute()
    {
        if (!BothRoms.Available)
            Skip = "Both USA ROMs are needed: \"Resident Evil 2 (USA).z64\" and \"... (Rev 1).z64\".";
    }
}
