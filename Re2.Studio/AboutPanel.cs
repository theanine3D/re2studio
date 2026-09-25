using System;
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;

namespace Re2.Studio;

/// <summary>The About tab.</summary>
public static class AboutPanel
{
    // --------------------------------------------------------------------- Edit these.

    /// <summary>Title line, shown large at the top.</summary>
    public const string Title = "RE2 Studio - by Theanine3D";

    /// <summary>The author's site. Shown as a clickable link.</summary>
    public const string Website = "https://www.youtube.com/@Theanine3D";

    /// <summary>Where the latest build can be downloaded.</summary>
    public const string DownloadUrl = "https://www.github.com/theanine3d/re2studio";

    /// <summary>Shown next to the title. Free text -- it is not read by anything else.</summary>
    public const string Version = "1.1";

    /// <summary>One-line description under the title.</summary>
    public const string Description =
        "An asset editor for Resident Evil 2 on the Nintendo 64.";

    // ---------------------------------------------------------------------

    private static string _status = "";

    public static void Draw()
    {
        ImGui.Dummy(new Vector2(0, 8));

        ImGui.TextUnformatted(Title);
        ImGui.SameLine();
        ImGui.TextDisabled($"v{Version}");

        if (Description.Length > 0)
        {
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.TextWrapped(Description);
        }

        ImGui.Dummy(new Vector2(0, 10));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 6));

        ImGui.TextUnformatted("Website");
        Hyperlink(Website);

        if (DownloadUrl.Length > 0 && DownloadUrl != Website)
        {
            ImGui.Dummy(new Vector2(0, 8));
            ImGui.TextUnformatted("Latest version");
            Hyperlink(DownloadUrl);
        }
        else
        {
            ImGui.Dummy(new Vector2(0, 8));
            ImGui.TextDisabled("The latest version is posted at the link above.");
        }

        if (_status.Length > 0)
        {
            ImGui.Dummy(new Vector2(0, 10));
            ImGui.TextDisabled(_status);
        }
    }

    /// <summary>The credit and link on their own, centred, for the welcome screen.</summary>
    public static void DrawCompact()
    {
        CentreNext(Title);
        ImGui.TextDisabled(Title);

        CentreNext(Website);
        Hyperlink(Website);
    }

    /// <summary>Positions the cursor so the next item of this width is centred in the window.</summary>
    private static void CentreNext(string text)
        => ImGui.SetCursorPosX((ImGui.GetWindowWidth() - ImGui.CalcTextSize(text).X) * 0.5f);

    /// <summary>A clickable link: coloured, underlined, and it opens the system browser.</summary>
    private static void Hyperlink(string url)
    {
        var colour = new Vector4(0.40f, 0.70f, 1.00f, 1f);
        var hovered = new Vector4(0.62f, 0.82f, 1.00f, 1f);

        bool over = false;
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextUnformatted(url);
        ImGui.PopStyleColor();

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        if (ImGui.IsItemHovered())
        {
            over = true;
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Open in your browser");
        }

        ImGui.GetWindowDrawList().AddLine(
            new Vector2(min.X, max.Y), new Vector2(max.X, max.Y),
            ImGui.GetColorU32(over ? hovered : colour));

        if (ImGui.IsItemClicked()) Open(url);
    }

    /// <summary>Hands a URL to the system browser.</summary>
    private static void Open(string url)
    {
        if (!IsBrowsable(url))
        {
            _status = "Not opened: only http and https links are followed.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _status = "Opened " + url;
        }
        catch (Exception ex)
        {
            _status = "Could not open the link: " + ex.Message;
        }
    }

    /// <summary>Whether a string is a link this tab is willing to hand to the browser.</summary>
    public static bool IsBrowsable(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed)
           && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}
