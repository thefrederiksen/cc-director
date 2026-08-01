using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests.Browsers;

public class BrowserLauncherTests
{
    // A trimmed Local State document mirroring the real info_cache shape (folder -> name/user_name/gaia_name).
    private const string SampleLocalState = """
    {
      "profile": {
        "info_cache": {
          "Default": { "name": "Person 1", "user_name": "", "gaia_name": "" },
          "Profile 2": { "name": "Example Work", "user_name": "user1@example.com", "gaia_name": "Example User 1" },
          "Profile 1": { "name": "Example Personal", "user_name": "user2@example.com", "gaia_name": "Example User 2" }
        }
      }
    }
    """;

    [Fact]
    public void ParseProfiles_ReadsFolderNameDisplayNameAndAccount()
    {
        var profiles = BrowserLauncher.ParseProfiles(SampleLocalState);

        var center = Assert.Single(profiles, p => p.FolderName == "Profile 2");
        Assert.Equal("Example Work", center.DisplayName);
        Assert.Equal("user1@example.com", center.Account);
    }

    [Fact]
    public void ParseProfiles_NoAccount_UsesNullAccount()
    {
        var profiles = BrowserLauncher.ParseProfiles(SampleLocalState);

        var person = Assert.Single(profiles, p => p.FolderName == "Default");
        Assert.Equal("Person 1", person.DisplayName);
        Assert.Null(person.Account);
    }

    [Fact]
    public void ParseProfiles_FallsBackToGaiaNameWhenUserNameEmpty()
    {
        const string json = """
        { "profile": { "info_cache": {
            "Profile 1": { "name": "Cody", "user_name": "", "gaia_name": "Sample Profile" }
        } } }
        """;

        var profile = Assert.Single(BrowserLauncher.ParseProfiles(json));
        Assert.Equal("Sample Profile", profile.Account);
    }

    [Fact]
    public void ParseProfiles_SortsAccountBearingProfilesFirstThenByName()
    {
        var profiles = BrowserLauncher.ParseProfiles(SampleLocalState);

        // Account-bearing profiles (Example Work, Example Personal) come before the
        // accountless "Person 1", and within the account group they sort by display name.
        Assert.Equal(new[] { "Profile 1", "Profile 2", "Default" }, profiles.Select(p => p.FolderName).ToArray());
    }

    [Fact]
    public void ParseProfiles_MissingInfoCache_ReturnsEmpty()
    {
        Assert.Empty(BrowserLauncher.ParseProfiles("{ \"profile\": {} }"));
    }

    [Fact]
    public void ParseProfiles_EmptyJson_Throws()
    {
        Assert.Throws<ArgumentException>(() => BrowserLauncher.ParseProfiles(""));
    }

    [Fact]
    public void GetProfiles_ReadsFromBrowserLocalStateFile()
    {
        var userDataDir = Path.Combine(Path.GetTempPath(), "cc-director-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDir);
        try
        {
            File.WriteAllText(Path.Combine(userDataDir, "Local State"), SampleLocalState);
            var browser = new BrowserInfo(BrowserKind.Edge, "Microsoft Edge", "C:\\nonexistent\\msedge.exe", userDataDir);

            var profiles = BrowserLauncher.GetProfiles(browser);

            Assert.Equal(3, profiles.Count);
            Assert.Contains(profiles, p => p.FolderName == "Profile 1");
        }
        finally
        {
            Directory.Delete(userDataDir, recursive: true);
        }
    }

    // The macOS table is the one a Windows test run would never otherwise look at, and getting it
    // wrong is exactly the bug that made a Mac holding both browsers report neither installed. These
    // assert its shape from any host OS, so a Windows build still fails when the Mac paths regress.

    // Path.Combine uses the HOST separator, so a Windows test run sees backslashes in the macOS
    // table. Normalising here keeps the assertions about the SHAPE of the path, which is the thing
    // that was wrong, rather than about which machine happened to run the test.
    private static string Slashes(string path) => path.Replace('\\', '/');

    [Fact]
    public void MacCandidates_PointAtTheBinaryInsideTheBundleNotTheBundleItself()
    {
        var chrome = Assert.Single(BrowserLauncher.MacCandidates(), c => c.Kind == BrowserKind.Chrome);

        // A path stopping at the .app is a folder, and Process.Start cannot run a folder - every
        // candidate must reach the real binary under Contents/MacOS.
        Assert.All(chrome.ExeCandidates, p => Assert.EndsWith(".app/Contents/MacOS/Google Chrome", Slashes(p)));
        Assert.Contains("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            chrome.ExeCandidates.Select(Slashes));
    }

    [Fact]
    public void MacCandidates_PreferSystemApplicationsOverThePerUserOne()
    {
        var edge = Assert.Single(BrowserLauncher.MacCandidates(), c => c.Kind == BrowserKind.Edge);

        Assert.Equal("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            Slashes(edge.ExeCandidates[0]));
        Assert.EndsWith("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            Slashes(edge.ExeCandidates[1]));
        Assert.NotEqual(Slashes(edge.ExeCandidates[0]), Slashes(edge.ExeCandidates[1]));
    }

    [Fact]
    public void MacCandidates_UseTheMacUserDataShapeNotTheWindowsOne()
    {
        var mac = BrowserLauncher.MacCandidates();
        var chrome = Slashes(Assert.Single(mac, c => c.Kind == BrowserKind.Chrome).UserDataDir);
        var edge = Slashes(Assert.Single(mac, c => c.Kind == BrowserKind.Edge).UserDataDir);

        // macOS keeps profiles directly under Application Support - there is no "User Data" level,
        // and Edge is one flat folder rather than the Windows Microsoft/Edge pair.
        Assert.EndsWith("Library/Application Support/Google/Chrome", chrome);
        Assert.EndsWith("Library/Application Support/Microsoft Edge", edge);
        Assert.DoesNotContain("User Data", chrome);
        Assert.DoesNotContain("User Data", edge);
    }

    [Fact]
    public void WindowsCandidates_StillUseTheWindowsUserDataShape()
    {
        var windows = BrowserLauncher.WindowsCandidates();

        // The Chromium-standard shape, which is every browser here EXCEPT Opera - Opera keeps its
        // profile in Roaming with no "User Data" level, asserted on its own below. Blanket-asserting
        // "User Data" across the whole table would have made the Opera row unaddable rather than
        // catching a real defect.
        Assert.All(windows.Where(c => c.Kind != BrowserKind.Opera),
            c => Assert.EndsWith("User Data", c.UserDataDir));
        Assert.All(windows, c => Assert.All(c.ExeCandidates, p => Assert.EndsWith(".exe", p)));
    }

    [Fact]
    public void WindowsCandidates_OperaUsesRoamingAppDataWithNoUserDataLevel()
    {
        var opera = Assert.Single(BrowserLauncher.WindowsCandidates(), c => c.Kind == BrowserKind.Opera);

        // Opera's profile is NOT where the Chromium pattern says: it is under Roaming, and the folder
        // is "Opera Stable" rather than a "User Data" level. Reading Local State from a Chrome-shaped
        // guess finds nothing and the browser silently reports no signed-in account.
        Assert.EndsWith(Path.Combine("Opera Software", "Opera Stable"), opera.UserDataDir);
        Assert.DoesNotContain("User Data", opera.UserDataDir);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), opera.UserDataDir);

        // The installer defaults to a per-user install, so that path must be tried before the
        // all-users one or the default install is the case we miss.
        Assert.StartsWith(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Opera"),
            opera.ExeCandidates[0]);
    }

    [Fact]
    public void WindowsCandidates_BraveUsesItsOwnVendorFolder()
    {
        var brave = Assert.Single(BrowserLauncher.WindowsCandidates(), c => c.Kind == BrowserKind.Brave);

        Assert.All(brave.ExeCandidates, p => Assert.EndsWith(Path.Combine("Brave-Browser", "Application", "brave.exe"), p));
        Assert.EndsWith(Path.Combine("BraveSoftware", "Brave-Browser", "User Data"), brave.UserDataDir);
    }

    [Fact]
    public void MacCandidates_BraveAndOperaReachTheBinaryAndTheirOwnSupportFolders()
    {
        var mac = BrowserLauncher.MacCandidates();
        var brave = Assert.Single(mac, c => c.Kind == BrowserKind.Brave);
        var opera = Assert.Single(mac, c => c.Kind == BrowserKind.Opera);

        Assert.All(brave.ExeCandidates, p => Assert.EndsWith(".app/Contents/MacOS/Brave Browser", Slashes(p)));
        Assert.All(opera.ExeCandidates, p => Assert.EndsWith(".app/Contents/MacOS/Opera", Slashes(p)));

        // Neither follows the product name: Brave nests under a vendor folder, and Opera's support
        // folder is named by BUNDLE ID. Both are the kind of thing a pattern-match gets wrong.
        Assert.EndsWith("Library/Application Support/BraveSoftware/Brave-Browser", Slashes(brave.UserDataDir));
        Assert.EndsWith("Library/Application Support/com.operasoftware.Opera", Slashes(opera.UserDataDir));
        Assert.DoesNotContain("User Data", Slashes(brave.UserDataDir));
        Assert.DoesNotContain("User Data", Slashes(opera.UserDataDir));
    }

    [Fact]
    public void EveryBrowserKind_HasAWindowsAndAMacCandidateRow()
    {
        // The bug this catches is an enum member added without its install locations: the browser
        // appears in the API's accepted list and in error messages, then reports "not installed" on
        // every machine because nothing ever looks for it.
        foreach (var kind in Enum.GetValues<BrowserKind>())
        {
            Assert.Single(BrowserLauncher.WindowsCandidates(), c => c.Kind == kind);
            Assert.Single(BrowserLauncher.MacCandidates(), c => c.Kind == kind);
        }
    }

    [Fact]
    public void GetProfiles_NoLocalStateFile_ReturnsEmpty()
    {
        var browser = new BrowserInfo(BrowserKind.Chrome, "Google Chrome", "C:\\nonexistent\\chrome.exe",
            Path.Combine(Path.GetTempPath(), "cc-director-missing-" + Guid.NewGuid().ToString("N")));

        Assert.Empty(BrowserLauncher.GetProfiles(browser));
    }

    [Fact]
    public void OpenWithProfile_MissingExe_ThrowsNamingTheExe()
    {
        var browser = new BrowserInfo(BrowserKind.Edge, "Microsoft Edge", "C:\\nope\\msedge.exe", "C:\\nope\\User Data");

        var ex = Assert.Throws<FileNotFoundException>(
            () => BrowserLauncher.OpenWithProfile("https://example.com", browser, "Profile 1"));
        Assert.Contains("msedge.exe", ex.Message);
    }

    [Fact]
    public void OpenWithProfile_MissingProfileFolder_ThrowsNamingTheProfile()
    {
        // Any real existing executable will do - it only has to make the exe check pass so the
        // profile-folder check is the one that fails. cmd.exe does not exist on a Mac, which turned
        // this into a red test there (it threw FileNotFoundException for the exe instead), so the
        // ingredient is chosen per platform. The behaviour under test is platform-neutral and now
        // actually runs on both, rather than being skipped on one.
        var fakeExe = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : "/bin/sh";
        Assert.True(File.Exists(fakeExe), $"this test needs a real executable to exist at {fakeExe}");
        var userDataDir = Path.Combine(Path.GetTempPath(), "cc-director-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDir);
        try
        {
            var browser = new BrowserInfo(BrowserKind.Edge, "Microsoft Edge", fakeExe, userDataDir);

            var ex = Assert.Throws<DirectoryNotFoundException>(
                () => BrowserLauncher.OpenWithProfile("https://example.com", browser, "Profile 9"));
            Assert.Contains("Profile 9", ex.Message);
        }
        finally
        {
            Directory.Delete(userDataDir, recursive: true);
        }
    }
}
