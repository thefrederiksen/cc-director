using System.Text.Json;
using CcDirector.Core.Browsers;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Browsers;

/// <summary>
/// Tests for <see cref="BrowserDefaultStore"/>, focused on the per-repository default added for
/// GitHub #1112 and the resolve order it introduces: repository default -> application-wide default
/// -> operating-system default (a null result). Every method runs against an isolated
/// CC_DIRECTOR_ROOT so it reads and writes a throwaway config.json; xUnit runs a class's methods
/// sequentially, and the "CcStorageRoot" collection keeps this class from racing the other classes
/// that redirect the process-wide root.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class BrowserDefaultStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public BrowserDefaultStoreTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-browserdefault-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string RepoPath = @"D:\Repos\WorkRepo";
    private const string OtherRepoPath = @"D:\Repos\PersonalRepo";

    private static BrowserDefault GlobalDefault => new(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Default");
    private static BrowserDefault RepoDefault => new(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Profile 2");

    // -- Resolve order: repo default -> global default -> OS default (null) --

    [Fact]
    public void Resolve_RepoHasOwnDefault_ReturnsRepoDefault()
    {
        BrowserDefaultStore.Save(GlobalDefault);
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        var resolved = BrowserDefaultStore.Resolve(RepoPath);

        Assert.Equal(RepoDefault, resolved);
    }

    [Fact]
    public void Resolve_RepoHasNoDefault_FallsBackToGlobalDefault()
    {
        BrowserDefaultStore.Save(GlobalDefault);
        // Only OtherRepoPath has a repo default; RepoPath does not.
        BrowserDefaultStore.SaveForRepo(OtherRepoPath, RepoDefault);

        var resolved = BrowserDefaultStore.Resolve(RepoPath);

        Assert.Equal(GlobalDefault, resolved);
    }

    [Fact]
    public void Resolve_NeitherRepoNorGlobal_ReturnsNullForOsDefault()
    {
        var resolved = BrowserDefaultStore.Resolve(RepoPath);

        // Null legitimately means "fall through to the operating-system default browser".
        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_NullRepoPath_UsesGlobalDefaultOnly()
    {
        BrowserDefaultStore.Save(GlobalDefault);
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        // A link with no owning repository never sees any repo default.
        var resolved = BrowserDefaultStore.Resolve(null);

        Assert.Equal(GlobalDefault, resolved);
    }

    // -- Durability + isolation between the two levels --

    [Fact]
    public void SaveForRepo_RoundTripsThroughDisk()
    {
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        // LoadForRepo reads fresh from config.json, so this asserts the persisted value.
        Assert.Equal(RepoDefault, BrowserDefaultStore.LoadForRepo(RepoPath));
    }

    [Fact]
    public void SaveForRepo_DoesNotDisturbGlobalDefault()
    {
        BrowserDefaultStore.Save(GlobalDefault);

        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        // The application-wide default is untouched by a repository write.
        Assert.Equal(GlobalDefault, BrowserDefaultStore.Load());
    }

    [Fact]
    public void SaveForRepo_TwoRepos_KeepBothIndependently()
    {
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);
        BrowserDefaultStore.SaveForRepo(OtherRepoPath, GlobalDefault);

        Assert.Equal(RepoDefault, BrowserDefaultStore.LoadForRepo(RepoPath));
        Assert.Equal(GlobalDefault, BrowserDefaultStore.LoadForRepo(OtherRepoPath));
    }

    [Fact]
    public void SaveGlobal_DoesNotDisturbExistingRepoDefault()
    {
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        BrowserDefaultStore.Save(GlobalDefault);

        Assert.Equal(RepoDefault, BrowserDefaultStore.LoadForRepo(RepoPath));
    }

    [Fact]
    public void SaveForRepo_PreservesUnrelatedConfigSections()
    {
        var patch = new System.Text.Json.Nodes.JsonObject
        {
            ["gateway"] = new System.Text.Json.Nodes.JsonObject { ["url"] = "http://gw.example:7878" },
        };
        CcDirectorConfigService.MergePatch(patch);

        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        var root = CcDirectorConfigService.ReadRaw();
        Assert.Equal("http://gw.example:7878", (string?)root["gateway"]?["url"]);
    }

    // -- Key normalization: the same repo resolves regardless of separator/case/trailing slash --

    [Fact]
    public void LoadForRepo_NormalizesSeparatorsSlashAndTrailingSlash()
    {
        BrowserDefaultStore.SaveForRepo(@"D:\Repos\WorkRepo", RepoDefault);

        // Forward slashes and a trailing slash must map to the same stored entry.
        Assert.Equal(RepoDefault, BrowserDefaultStore.LoadForRepo("D:/Repos/WorkRepo/"));
    }

    [Fact]
    public void LoadForRepo_OnWindowsIsCaseInsensitive()
    {
        if (!OperatingSystem.IsWindows())
            return; // Path case-insensitivity is a Windows property; skip elsewhere.

        BrowserDefaultStore.SaveForRepo(@"D:\Repos\WorkRepo", RepoDefault);

        Assert.Equal(RepoDefault, BrowserDefaultStore.LoadForRepo(@"d:\repos\workrepo"));
    }

    [Fact]
    public void LoadForRepo_UnknownRepo_ReturnsNull()
    {
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        Assert.Null(BrowserDefaultStore.LoadForRepo(@"D:\Repos\NeverSet"));
    }

    [Fact]
    public void LoadForRepo_BlankPath_ReturnsNull()
    {
        Assert.Null(BrowserDefaultStore.LoadForRepo(""));
        Assert.Null(BrowserDefaultStore.LoadForRepo("   "));
    }

    [Fact]
    public void SaveForRepo_BlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => BrowserDefaultStore.SaveForRepo("", RepoDefault));
    }

    [Fact]
    public void SaveForRepo_StoresUnderBrowserRepoDefaultsSection()
    {
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        var json = File.ReadAllText(CcStorage.ConfigJson());
        using var doc = JsonDocument.Parse(json);
        var repoDefaults = doc.RootElement.GetProperty("browser").GetProperty("repoDefaults");
        // Windows key is the lowercased, backslash-normalized path.
        var expectedKey = OperatingSystem.IsWindows() ? RepoPath.ToLowerInvariant() : RepoPath;
        var entry = repoDefaults.GetProperty(expectedKey);
        Assert.Equal(RepoDefault.ExePath, entry.GetProperty("exePath").GetString());
        Assert.Equal(RepoDefault.ProfileFolder, entry.GetProperty("profileFolder").GetString());
    }

    // -- Clearing: how the user takes a default back (picking "System default" in the picker and
    //    asking to remember it). Without these, a default set once could never be removed.

    [Fact]
    public void Clear_AfterSave_ResolvesToOperatingSystemDefault()
    {
        BrowserDefaultStore.Save(GlobalDefault);

        BrowserDefaultStore.Clear();

        Assert.Null(BrowserDefaultStore.Load());
        Assert.Null(BrowserDefaultStore.Resolve(null));
    }

    [Fact]
    public void Clear_NothingSaved_IsANoOp()
    {
        BrowserDefaultStore.Clear();

        Assert.Null(BrowserDefaultStore.Load());
    }

    [Fact]
    public void ClearForRepo_AfterSaveForRepo_FallsBackToGlobalDefault()
    {
        BrowserDefaultStore.Save(GlobalDefault);
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        BrowserDefaultStore.ClearForRepo(RepoPath);

        Assert.Null(BrowserDefaultStore.LoadForRepo(RepoPath));
        Assert.Equal(GlobalDefault, BrowserDefaultStore.Resolve(RepoPath));
    }

    [Fact]
    public void ClearForRepo_LeavesOtherRepositoriesAlone()
    {
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);
        BrowserDefaultStore.SaveForRepo(OtherRepoPath, GlobalDefault);

        BrowserDefaultStore.ClearForRepo(RepoPath);

        Assert.Null(BrowserDefaultStore.LoadForRepo(RepoPath));
        Assert.Equal(GlobalDefault, BrowserDefaultStore.LoadForRepo(OtherRepoPath));
    }

    [Fact]
    public void ClearForRepo_LeavesTheGlobalDefaultAlone()
    {
        BrowserDefaultStore.Save(GlobalDefault);
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        BrowserDefaultStore.ClearForRepo(RepoPath);

        Assert.Equal(GlobalDefault, BrowserDefaultStore.Load());
    }

    [Fact]
    public void Clear_LeavesRepositoryDefaultsAlone()
    {
        BrowserDefaultStore.Save(GlobalDefault);
        BrowserDefaultStore.SaveForRepo(RepoPath, RepoDefault);

        BrowserDefaultStore.Clear();

        Assert.Equal(RepoDefault, BrowserDefaultStore.LoadForRepo(RepoPath));
        Assert.Equal(RepoDefault, BrowserDefaultStore.Resolve(RepoPath));
    }

    [Fact]
    public void Clear_PreservesUnrelatedConfigKeys()
    {
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject
        {
            ["someOtherSection"] = new System.Text.Json.Nodes.JsonObject { ["keep"] = "me" }
        });
        BrowserDefaultStore.Save(GlobalDefault);

        BrowserDefaultStore.Clear();

        var root = CcDirectorConfigService.ReadRaw();
        Assert.Equal("me", (string?)root["someOtherSection"]?["keep"]);
    }

    [Fact]
    public void ClearForRepo_BlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => BrowserDefaultStore.ClearForRepo(""));
    }
}
