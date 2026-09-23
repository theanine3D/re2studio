using Re2.Studio;
using Xunit;

namespace Re2.Tests;

/// <summary>
/// The About tab hands its link to the system shell, which will run whatever a string names.
/// </summary>
public class AboutPanelTests
{
    [Theory]
    [InlineData("https://www.youtube.com/@Theanine3D")]
    [InlineData("http://example.com/downloads")]
    public void FollowsWebLinks(string url) => Assert.True(AboutPanel.IsBrowsable(url));

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("C:/Windows/System32/cmd.exe")]
    [InlineData("cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void RefusesAnythingElse(string url) => Assert.False(AboutPanel.IsBrowsable(url));

    /// <summary>
    /// The links actually shipped in the tab have to pass the check, or the tab would refuse its own
    /// buttons -- an easy thing to break by editing the constants.
    /// </summary>
    [Fact]
    public void TheConfiguredLinksAreFollowable()
    {
        Assert.True(AboutPanel.IsBrowsable(AboutPanel.Website),
                    $"Website is not an http/https link: '{AboutPanel.Website}'");
        Assert.True(AboutPanel.IsBrowsable(AboutPanel.DownloadUrl),
                    $"DownloadUrl is not an http/https link: '{AboutPanel.DownloadUrl}'");
        Assert.NotEqual("", AboutPanel.Title);
    }
}
