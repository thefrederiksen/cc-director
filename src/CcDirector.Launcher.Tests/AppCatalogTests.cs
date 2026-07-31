using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The application catalogue, tested over a temporary directory tree rather than the machine's real Start
/// Menu, so the assertions are about the catalogue and not about what happens to be installed on the machine
/// running the tests.
///
/// Entries are the platform's own application file type, because that is what the catalogue collects: a
/// shortcut on Windows, a desktop entry elsewhere. The files need no real content - the catalogue reports
/// where a thing is and what it is called, and leaves starting it to the launch service.
/// </summary>
public sealed class AppCatalogTests : IDisposable
{
    private readonly string _root;

    public AppCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-app-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temporary directory that outlives the test run is not a test failure */ }
    }

    /// <summary>
    /// Create one application entry of whatever kind THIS operating system's catalogue actually collects.
    ///
    /// The three platforms do not merely differ in extension, which is what an earlier version of this helper
    /// assumed - it created a ".desktop" file on anything that was not Windows, and every one of these tests
    /// failed on macOS because a macOS application is a ".app" BUNDLE DIRECTORY and the catalogue is right to
    /// ignore a file by that name. The product code was correct and the helper was wrong, which is the more
    /// dangerous way round: it looked like nine real failures.
    /// </summary>
    private string CreateApp(string name, string? subdirectory = null)
    {
        var directory = subdirectory is null ? _root : Path.Combine(_root, subdirectory);
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsMacOS())
        {
            // A bundle is a directory, and the catalogue reports the directory itself as one item.
            var bundle = Path.Combine(directory, name + ".app");
            Directory.CreateDirectory(bundle);
            return bundle;
        }

        var path = Path.Combine(directory, name + (OperatingSystem.IsWindows() ? ".lnk" : ".desktop"));
        File.WriteAllText(path, "");
        return path;
    }

    private AppCatalog CatalogOverRoot() => new(new[] { (_root, "test-root") });

    [Fact]
    public void Search_EmptyQuery_ReturnsEveryApplication()
    {
        CreateApp("Chrome");
        CreateApp("Notepad");

        var result = CatalogOverRoot().Search("", 100);

        Assert.Equal(2, result.TotalMatches);
        Assert.Contains(result.Apps, app => app.Name == "Chrome");
        Assert.Contains(result.Apps, app => app.Name == "Notepad");
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Search_NestedDirectories_FindsApplicationsBelowTheRoot()
    {
        CreateApp("Visual Studio Code", subdirectory: "Microsoft");

        var result = CatalogOverRoot().Search("Visual", 100);

        Assert.Single(result.Apps);
        Assert.Equal("Visual Studio Code", result.Apps[0].Name);
    }

    [Fact]
    public void Search_NonApplicationFiles_AreIgnored()
    {
        CreateApp("Chrome");
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "not an application");

        var result = CatalogOverRoot().Search("", 100);

        Assert.Single(result.Apps);
        Assert.Equal("Chrome", result.Apps[0].Name);
    }

    /// <summary>
    /// A truncated catalogue must announce itself. Silently returning the first few would be indistinguishable
    /// from a machine with only a few programs installed.
    /// </summary>
    [Fact]
    public void Search_MoreMatchesThanTheLimit_ReportsTruncatedAndTheRealTotal()
    {
        for (var index = 0; index < 5; index++)
            CreateApp($"App{index}");

        var result = CatalogOverRoot().Search("", 2);

        Assert.Equal(5, result.TotalMatches);
        Assert.Equal(2, result.Apps.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Resolve_ExactName_WinsOverALongerNameContainingIt()
    {
        CreateApp("Notepad");
        CreateApp("Notepad++");

        var outcome = CatalogOverRoot().Resolve("Notepad");

        Assert.Equal(AppCatalog.ResolveStatus.Found, outcome.Status);
        Assert.Equal("Notepad", outcome.App!.Name);
    }

    [Fact]
    public void Resolve_SingleSubstringMatch_Resolves()
    {
        CreateApp("Google Chrome");

        var outcome = CatalogOverRoot().Resolve("chrome");

        Assert.Equal(AppCatalog.ResolveStatus.Found, outcome.Status);
        Assert.Equal("Google Chrome", outcome.App!.Name);
    }

    /// <summary>
    /// The rule that keeps a remote caller from starting the wrong program: when a name picks out more than
    /// one application and none of them matches exactly, the launcher refuses rather than guessing.
    /// </summary>
    [Fact]
    public void Resolve_SeveralSubstringMatches_IsAmbiguousRatherThanTheFirstOne()
    {
        CreateApp("Chrome Canary");
        CreateApp("Chrome Beta");

        var outcome = CatalogOverRoot().Resolve("Chrome");

        Assert.Equal(AppCatalog.ResolveStatus.Ambiguous, outcome.Status);
        Assert.Null(outcome.App);
        Assert.Equal(2, outcome.Candidates!.Count);
    }

    [Fact]
    public void Resolve_UnknownName_IsNotFound()
    {
        CreateApp("Chrome");

        Assert.Equal(AppCatalog.ResolveStatus.NotFound, CatalogOverRoot().Resolve("Photoshop").Status);
    }

    [Fact]
    public void ResolveLaunchPath_CataloguedPath_WinsOverTheApplicationName()
    {
        // Tenant-boundary hardening (CR-5): the path form is an allowlist lookup now. A path that IS a
        // catalogue entry still wins over the name beside it, exactly as the free path form used to.
        CreateApp("Chrome");
        var edge = CreateApp("Edge");

        var (path, error) = CatalogOverRoot().ResolveLaunchPath(edge, "Chrome");

        Assert.Null(error);
        Assert.Equal(edge, path);
    }

    /// <summary>
    /// The CR-5 refusal: a path that is not an entry of this machine's installed-applications catalogue is
    /// refused, whatever it points at. This is what stops a stolen key being remote code execution - the
    /// caller can no longer start an executable it dropped or found on the machine.
    /// </summary>
    [Fact]
    public void ResolveLaunchPath_UncataloguedPath_IsRefusedWithAReason()
    {
        CreateApp("Chrome");
        var dropped = Path.Combine(_root, "dropped-payload.exe");
        File.WriteAllText(dropped, "");

        var (path, error) = CatalogOverRoot().ResolveLaunchPath(dropped, null);

        Assert.Null(path);
        Assert.Contains("not in the installed-applications catalogue", error);
        Assert.Contains(Environment.MachineName, error);
    }

    [Fact]
    public void ResolveLaunchPath_CataloguedPath_IsAcceptedCaseInsensitively()
    {
        var chrome = CreateApp("Chrome");

        var (path, error) = CatalogOverRoot().ResolveLaunchPath(chrome.ToUpperInvariant(), null);

        Assert.Null(error);
        Assert.Equal(chrome, path);
    }

    [Fact]
    public void ResolveLaunchPath_ApplicationName_ResolvesToItsCataloguePath()
    {
        var expected = CreateApp("Chrome");

        var (path, error) = CatalogOverRoot().ResolveLaunchPath(null, "Chrome");

        Assert.Null(error);
        Assert.Equal(expected, path);
    }

    [Fact]
    public void ResolveLaunchPath_NeitherPathNorName_IsRefusedWithAReason()
    {
        var (path, error) = CatalogOverRoot().ResolveLaunchPath(null, null);

        Assert.Null(path);
        Assert.Contains("either path or app is required", error);
    }

    /// <summary>The ambiguity refusal must name the candidates, or the caller cannot act on it.</summary>
    [Fact]
    public void ResolveLaunchPath_AmbiguousName_RefusesAndNamesTheCandidates()
    {
        CreateApp("Chrome Canary");
        CreateApp("Chrome Beta");

        var (path, error) = CatalogOverRoot().ResolveLaunchPath(null, "Chrome");

        Assert.Null(path);
        Assert.Contains("Chrome Canary", error);
        Assert.Contains("Chrome Beta", error);
    }

    [Fact]
    public void ResolveLaunchPath_UnknownName_RefusesAndNamesTheMachine()
    {
        var (path, error) = CatalogOverRoot().ResolveLaunchPath(null, "Photoshop");

        Assert.Null(path);
        Assert.Contains("Photoshop", error);
        Assert.Contains(Environment.MachineName, error);
    }
}
