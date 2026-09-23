using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>The Linux file-dialog filter translation.</summary>
public sealed class LinuxDesktopTests
{
    [Fact]
    public void Win32FiltersBecomeLabelledPatternLists()
    {
        var filters = LinuxDesktop.ParseFilter(NativeDialogs.RomFilter);

        Assert.Equal(2, filters.Count);
        Assert.Equal("Nintendo 64 ROMs", filters[0].Label);
        Assert.Equal(new[] { "*.z64", "*.n64", "*.v64" }, filters[0].Patterns);
        Assert.Equal("All files", filters[1].Label);
        Assert.Equal(new[] { "*.*" }, filters[1].Patterns);
    }

    [Fact]
    public void AnEmptyFilterGivesNoEntries()
        => Assert.Empty(LinuxDesktop.ParseFilter(""));
}
