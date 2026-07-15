using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Core.Utilities;
using CcDirector.Terminal.Avalonia;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Pins the SHAPE of the link context menu. The per-repository default (#1533) shipped its actions
/// behind a four-level hover chain ("Open in Browser" -> browser -> profile -> intent), which no
/// pointer could actually traverse: Avalonia's MenuItem has no hover-intent safe area, so moving
/// diagonally toward a submenu crosses the sibling row and collapses the chain. These tests fail if
/// anything reintroduces a submenu here.
/// </summary>
public class LinkContextMenuBuilderTests
{
    private static LinkMenuContext UrlContext(string? repoPath = null) => new()
    {
        Link = "https://example.com/page",
        Type = LinkDetector.LinkType.Url,
        RepoPath = repoPath,
        Owner = new Border(),
    };

    private static IEnumerable<MenuItem> ItemsOf(ContextMenu menu) => menu.Items.OfType<MenuItem>();

    [AvaloniaFact]
    public void Build_UrlLink_EveryItemIsFlat()
    {
        var menu = LinkContextMenuBuilder.Build(UrlContext(@"D:\repos\devthrottle"));

        Assert.NotEmpty(ItemsOf(menu));
        foreach (var item in ItemsOf(menu))
            Assert.Empty(item.Items);
    }

    [AvaloniaFact]
    public void Build_UrlLink_OffersOpenAndChooseBrowser()
    {
        var menu = LinkContextMenuBuilder.Build(UrlContext());

        var headers = ItemsOf(menu).Select(i => i.Header?.ToString()).ToList();
        Assert.Equal(new[] { "Copy URL", "Open in Browser", "Choose Browser..." }, headers);
    }

    [AvaloniaFact]
    public void Build_HtmlPath_OffersOpenAndChooseBrowser()
    {
        var menu = LinkContextMenuBuilder.Build(new LinkMenuContext
        {
            Link = @"D:\repos\devthrottle\report.html",
            Type = LinkDetector.LinkType.Path,
            Owner = new Border(),
        });

        var headers = ItemsOf(menu).Select(i => i.Header?.ToString()).ToList();
        Assert.Contains("Open in Browser", headers);
        Assert.Contains("Choose Browser...", headers);
        foreach (var item in ItemsOf(menu))
            Assert.Empty(item.Items);
    }

    /// <summary>
    /// The browser items must not read the disk while the menu is being built - detection used to
    /// run per-browser Local State reads on the UI thread here (CLAUDE.md rule 1). The picker does
    /// that work in the background instead. This asserts the menu builds with no browser rows in it
    /// at all, which is what makes the build I/O-free.
    /// </summary>
    [AvaloniaFact]
    public void Build_UrlLink_DoesNotEnumerateBrowsers()
    {
        var menu = LinkContextMenuBuilder.Build(UrlContext());

        var headers = ItemsOf(menu).Select(i => i.Header?.ToString() ?? "").ToList();
        Assert.DoesNotContain(headers, h => h.Contains("Chrome") || h.Contains("Edge"));
    }

    /// <summary>
    /// A link with no owning repository still gets the picker; the repository scope simply is not
    /// offered inside it. Nothing about the menu changes.
    /// </summary>
    [AvaloniaFact]
    public void Build_UrlLinkWithoutRepo_StillOffersChooseBrowser()
    {
        var menu = LinkContextMenuBuilder.Build(UrlContext(repoPath: null));

        Assert.Contains(ItemsOf(menu), i => i.Header?.ToString() == "Choose Browser...");
    }
}
