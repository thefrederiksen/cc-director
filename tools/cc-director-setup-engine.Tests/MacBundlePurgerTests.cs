using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The purge must converge a host on ONE canonical Director bundle without ever deleting
/// something that is not provably the product's own: developer slot wrappers share the
/// "CC Director N" names but carry suffixed bundle ids, and "Director" is generic enough
/// that an unrelated vendor's app could share the name outright (issue #1821).
/// </summary>
public class MacBundlePurgerTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _log = [];

    public MacBundlePurgerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"purger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string MakeBundle(string name, string? bundleId)
    {
        var contents = Path.Combine(_root, name, "Contents");
        Directory.CreateDirectory(contents);
        if (bundleId is not null)
        {
            File.WriteAllText(Path.Combine(contents, "Info.plist"),
                $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>CFBundleIdentifier</key>      <string>{bundleId}</string>
                    <key>CFBundleExecutable</key>      <string>launch</string>
                </dict>
                </plist>
                """);
        }
        return Path.Combine(_root, name);
    }

    private void Purge(string keepName = "Director.app") =>
        MacBundlePurger.Purge([_root], Path.Combine(_root, keepName), _log.Add,
            MacBundlePurger.ReadBundleIdentifier);

    [Fact]
    public void Purge_LegacyProductBundle_Deleted()
    {
        var legacy = MakeBundle("CC Director.app", MacBundlePurger.ProductBundleIdentifier);
        Purge();
        Assert.False(Directory.Exists(legacy));
        Assert.Contains(_log, l => l.Contains("removed stale bundle"));
    }

    [Fact]
    public void Purge_FinderNumberedDuplicates_Deleted()
    {
        var dup1 = MakeBundle("Director 2.app", MacBundlePurger.ProductBundleIdentifier);
        var dup2 = MakeBundle("CC Director 10.app", MacBundlePurger.ProductBundleIdentifier);
        Purge();
        Assert.False(Directory.Exists(dup1));
        Assert.False(Directory.Exists(dup2));
    }

    [Fact]
    public void Purge_KeepPath_Survives()
    {
        var keep = MakeBundle("Director.app", MacBundlePurger.ProductBundleIdentifier);
        Purge();
        Assert.True(Directory.Exists(keep));
    }

    [Fact]
    public void Purge_DeveloperSlotWrapper_SurvivesAndIsLogged()
    {
        // The developer slot wrappers live beside the real install on a development machine.
        // Their suffixed bundle id is the proof they are not the shipped Director.
        var slot = MakeBundle("CC Director 2.app", "com.devthrottle.ccdirector.slot2");
        Purge();
        Assert.True(Directory.Exists(slot));
        Assert.Contains(_log, l => l.Contains("not ours to delete"));
    }

    [Fact]
    public void Purge_UnrelatedAppSharingTheName_Survives()
    {
        var foreign = MakeBundle("Director.app", "com.adobe.director");
        Purge(keepName: "elsewhere/Director.app");
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void Purge_BundleWithoutInfoPlist_SurvivesAndIsLogged()
    {
        // No Info.plist means identity cannot be confirmed - the safe default is to leave it.
        var broken = MakeBundle("CC Director 3.app", bundleId: null);
        Purge();
        Assert.True(Directory.Exists(broken));
        Assert.Contains(_log, l => l.Contains("unreadable"));
    }

    [Fact]
    public void Purge_NameNotMatchingBundlePattern_NotTouched()
    {
        // "Directory.app" and non-numeric suffixes must never be considered, whatever their id.
        var directory = MakeBundle("Directory.app", MacBundlePurger.ProductBundleIdentifier);
        var devNamed = MakeBundle("Director Dev 2.app", MacBundlePurger.ProductBundleIdentifier);
        Purge();
        Assert.True(Directory.Exists(directory));
        Assert.True(Directory.Exists(devNamed));
    }

    [Fact]
    public void Purge_NonExistentDirectory_NoThrow()
    {
        MacBundlePurger.Purge([Path.Combine(_root, "missing")], Path.Combine(_root, "Director.app"), _log.Add);
        Assert.Empty(_log);
    }

    [Theory]
    [InlineData("Director.app", "Director", true)]
    [InlineData("Director 2.app", "Director", true)]
    [InlineData("CC Director 10.app", "CC Director", true)]
    [InlineData("Directory.app", "Director", false)]
    [InlineData("Director Dev.app", "Director", false)]
    [InlineData("Director 2b.app", "Director", false)]
    [InlineData("Director .app", "Director", false)]
    public void IsBundleName_MatchesOnlyExactAndNumberedForms(string name, string baseName, bool expected) =>
        Assert.Equal(expected, MacBundlePurger.IsBundleName(name, baseName));

    [Fact]
    public void ReadBundleIdentifier_ReadsXmlPlist()
    {
        var bundle = MakeBundle("Director.app", "com.devthrottle.ccdirector");
        Assert.Equal("com.devthrottle.ccdirector", MacBundlePurger.ReadBundleIdentifier(bundle));
    }

    [Fact]
    public void ReadBundleIdentifier_MissingPlist_ReturnsNull()
    {
        var bundle = MakeBundle("Director.app", bundleId: null);
        Assert.Null(MacBundlePurger.ReadBundleIdentifier(bundle));
    }
}
