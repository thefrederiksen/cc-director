using CcDirector.Avalonia.Fleet;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #1627: the fleet map's scope is remembered PER INSTALL, and is OFF by default.
///
/// Every test here writes into its own temp directory. None of them may touch
/// %LOCALAPPDATA%\cc-director - a test that reaches the real folder writes into the running Director's
/// own config, which is the exact class of bug StorageRootGuardTests exists to prevent.
/// </summary>
public sealed class FleetMapSettingsTests : IDisposable
{
    private readonly string _dir;

    public FleetMapSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fleetmap-settings-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a leftover temp dir is not a test failure */ }
    }

    private FleetMapSettings Settings(string slotKey = @"C:\installs\cc-director.exe")
        => new(directory: _dir, slotKey: slotKey);

    [Fact]
    public void LoadShowWholeFleet_OnAFirstRun_IsOff()
    {
        // The documented default: this cc-director's own sessions, because those are the ones a click
        // here can open. An absent file is a first run, not an error.
        Assert.False(Settings().LoadShowWholeFleet());
    }

    [Fact]
    public void SaveThenLoad_RemembersTheChoice()
    {
        Settings().SaveShowWholeFleet(true);
        Assert.True(Settings().LoadShowWholeFleet());

        Settings().SaveShowWholeFleet(false);
        Assert.False(Settings().LoadShowWholeFleet());
    }

    [Fact]
    public void TwoInstalls_RememberSeparately()
    {
        // The whole point of keying by exe path: cc-director.exe and cc-director1.exe are different
        // cc-directors and must not share an answer.
        var main = Settings(@"C:\installs\cc-director.exe");
        var slot1 = Settings(@"C:\installs\cc-director1.exe");

        main.SaveShowWholeFleet(true);

        Assert.True(main.LoadShowWholeFleet());
        Assert.False(slot1.LoadShowWholeFleet());
        Assert.NotEqual(main.FilePath, slot1.FilePath);
    }

    [Fact]
    public void TheSameInstall_ResolvesToTheSameFile_RegardlessOfPathSpelling()
    {
        // Windows paths are case-insensitive and mix slashes; the same install must not get two answers.
        var a = Settings(@"C:\installs\cc-director.exe");
        var b = Settings(@"c:/INSTALLS/CC-Director.EXE");
        Assert.Equal(a.FilePath, b.FilePath);

        a.SaveShowWholeFleet(true);
        Assert.True(b.LoadShowWholeFleet());
    }

    [Fact]
    public void AnUnreadableValue_FallsBackToTheDefault_RatherThanBreakingTheMap()
    {
        // A corrupted one-word preference file must not stop the fleet map opening. It reads as the
        // documented default and says so in the log.
        var s = Settings();
        File.WriteAllText(s.FilePath, "banana");
        Assert.False(s.LoadShowWholeFleet());
    }

    [Fact]
    public void TheSettingLivesBesideTheDirectorIdFile()
    {
        // It is keyed by the same slot as the Director's identity, in the folder CcStorage owns.
        var s = Settings();
        Assert.Equal(_dir, Path.GetDirectoryName(s.FilePath));
        Assert.StartsWith("fleet-map-", Path.GetFileName(s.FilePath));
        Assert.EndsWith(".txt", s.FilePath);
    }
}
