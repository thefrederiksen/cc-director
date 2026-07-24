using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests.Browsers;

/// <summary>
/// Tests for <see cref="AutomationBrowserRegistry"/>: the JSON CRUD, slug/id minting, name and port
/// uniqueness, the 9310+ port allocation, and the attach-environment formatting. Every method runs
/// against an isolated CC_DIRECTOR_ROOT so it reads and writes a throwaway registry.json. The
/// "CcStorageRoot" collection serializes all classes that redirect the process-wide root so they do
/// not race. Port allocation is driven with an injected probe, so nothing here opens a real socket.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class AutomationBrowserRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    // Always-free probe: makes Create allocate 9310, 9311, ... deterministically (claimed ports are
    // still skipped by the allocator, so successive creates step upward).
    private static readonly Func<int, bool> AllFree = _ => true;

    public AutomationBrowserRegistryTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-autobrowser-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // -- Create / Load round-trip --

    [Fact]
    public void Create_PersistsEntry_LoadReadsItBack()
    {
        var created = AutomationBrowserRegistry.Create("Center Consulting", BrowserKind.Chrome, AllFree);

        var all = AutomationBrowserRegistry.Load();
        var loaded = Assert.Single(all);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("Center Consulting", loaded.Name);
        Assert.Equal(BrowserKind.Chrome, loaded.Kind);
        Assert.Equal(created.Port, loaded.Port);
        Assert.Null(loaded.LastSignedInUtc);
        // The user-data-dir sits under the browsers root and is named for the id.
        Assert.Equal(Path.Combine(AutomationBrowserRegistry.RootDirectory(), created.Id), loaded.UserDataDir);
    }

    [Fact]
    public void Load_NoRegistryFile_ReturnsEmpty()
    {
        Assert.Empty(AutomationBrowserRegistry.Load());
    }

    [Fact]
    public void Create_MintsSlugIdFromName()
    {
        var created = AutomationBrowserRegistry.Create("Center Consulting", BrowserKind.Chrome, AllFree);
        Assert.Equal("center-consulting", created.Id);
    }

    // -- Name uniqueness --

    [Fact]
    public void Create_DuplicateName_Throws_CaseInsensitive()
    {
        AutomationBrowserRegistry.Create("Work", BrowserKind.Chrome, AllFree);
        var ex = Assert.Throws<InvalidOperationException>(
            () => AutomationBrowserRegistry.Create("work", BrowserKind.Edge, AllFree));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void Create_TwoNamesThatSlugToSameId_GetDistinctIds()
    {
        var a = AutomationBrowserRegistry.Create("Center Consulting", BrowserKind.Chrome, AllFree);
        // A different NAME (so the name-uniqueness rule allows it) that slugifies to the same base.
        var b = AutomationBrowserRegistry.Create("center consulting!", BrowserKind.Chrome, AllFree);
        Assert.Equal("center-consulting", a.Id);
        Assert.Equal("center-consulting-2", b.Id);
    }

    // -- Port allocation --

    [Fact]
    public void Create_AllocatesPortsFrom9310Upward()
    {
        var a = AutomationBrowserRegistry.Create("A", BrowserKind.Chrome, AllFree);
        var b = AutomationBrowserRegistry.Create("B", BrowserKind.Chrome, AllFree);
        Assert.Equal(AutomationBrowserRegistry.PortRangeStart, a.Port);      // 9310
        Assert.Equal(AutomationBrowserRegistry.PortRangeStart + 1, b.Port);  // 9311
    }

    [Fact]
    public void AllocatePort_SkipsClaimedAndBusyPorts()
    {
        var existing = new[]
        {
            new AutomationBrowser("a", "A", BrowserKind.Chrome, "dir-a", 9310, DateTime.UtcNow, null),
        };
        // 9311 is "busy" per the probe; 9310 is already claimed. Next free is 9312.
        Func<int, bool> probe = port => port != 9311;

        var port = AutomationBrowserRegistry.AllocatePort(existing, probe);

        Assert.Equal(9312, port);
    }

    [Fact]
    public void AllocatePort_RangeExhausted_Throws()
    {
        var existing = Enumerable.Empty<AutomationBrowser>().ToList();
        var ex = Assert.Throws<InvalidOperationException>(
            () => AutomationBrowserRegistry.AllocatePort(existing, _ => false));
        Assert.Contains("No free automation-browser port", ex.Message);
    }

    // -- Rename --

    [Fact]
    public void Rename_ChangesNameButKeepsIdPortAndDir()
    {
        var created = AutomationBrowserRegistry.Create("Old Name", BrowserKind.Chrome, AllFree);

        var renamed = AutomationBrowserRegistry.Rename("Old Name", "New Name");

        Assert.Equal("New Name", renamed.Name);
        Assert.Equal(created.Id, renamed.Id);
        Assert.Equal(created.Port, renamed.Port);
        Assert.Equal(created.UserDataDir, renamed.UserDataDir);
        Assert.Equal("New Name", AutomationBrowserRegistry.Get(created.Id).Name);
    }

    [Fact]
    public void Rename_ToAnExistingName_Throws()
    {
        AutomationBrowserRegistry.Create("One", BrowserKind.Chrome, AllFree);
        AutomationBrowserRegistry.Create("Two", BrowserKind.Chrome, AllFree);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AutomationBrowserRegistry.Rename("Two", "one"));
        Assert.Contains("already named", ex.Message);
    }

    // -- Remove --

    [Fact]
    public void RemoveEntry_DropsBrowser_GetThenThrows()
    {
        AutomationBrowserRegistry.Create("Gone", BrowserKind.Chrome, AllFree);

        AutomationBrowserRegistry.RemoveEntry("Gone");

        Assert.Empty(AutomationBrowserRegistry.Load());
        Assert.Throws<KeyNotFoundException>(() => AutomationBrowserRegistry.Get("Gone"));
    }

    // -- Sign-in marker --

    [Fact]
    public void MarkSignedIn_SetsLastSignedInUtc()
    {
        AutomationBrowserRegistry.Create("Acct", BrowserKind.Chrome, AllFree);
        Assert.Null(AutomationBrowserRegistry.Get("Acct").LastSignedInUtc);

        var updated = AutomationBrowserRegistry.MarkSignedIn("Acct");

        Assert.NotNull(updated.LastSignedInUtc);
        Assert.NotNull(AutomationBrowserRegistry.Get("Acct").LastSignedInUtc);
    }

    // -- Lookup --

    [Fact]
    public void Find_ByIdAndByName_BothResolve_UnknownIsNull()
    {
        var created = AutomationBrowserRegistry.Create("Look Me Up", BrowserKind.Chrome, AllFree);

        Assert.Equal(created.Id, AutomationBrowserRegistry.Find("look-me-up")?.Id);
        Assert.Equal(created.Id, AutomationBrowserRegistry.Find("Look Me Up")?.Id);
        Assert.Null(AutomationBrowserRegistry.Find("nope"));
    }

    // -- Attach environment formatting --

    [Fact]
    public void AttachInfoFor_UsesIdAsBuNameAndLoopbackPortUrl()
    {
        var browser = new AutomationBrowser("center-consulting", "Center Consulting", BrowserKind.Chrome,
            "dir", 9317, DateTime.UtcNow, null);

        var attach = AutomationBrowserRegistry.AttachInfoFor(browser);

        Assert.Equal("center-consulting", attach.BuName);
        Assert.Equal("http://127.0.0.1:9317", attach.BuCdpUrl);
    }

    // -- Slug rules --

    [Theory]
    [InlineData("Center Consulting", "center-consulting")]
    [InlineData("  Trim  Me  ", "trim-me")]
    [InlineData("Weird!!!Name??", "weird-name")]
    [InlineData("UPPER", "upper")]
    public void Slugify_ProducesCleanSlugs(string input, string expected)
    {
        Assert.Equal(expected, AutomationBrowserRegistry.Slugify(input));
    }
}
