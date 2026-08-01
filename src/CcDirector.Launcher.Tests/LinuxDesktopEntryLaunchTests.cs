using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Tenant-boundary hardening, Phase 5b, inspection finding M03-I2-02: the catalogue-only launch rule
/// deleted generic launch on Linux.
///
/// On Linux the application catalogue is the desktop entry directories, so every catalogue entry is a
/// ".desktop" FILE. Phase 1 narrowed the launch verb to accept only a catalogued path - correct - but
/// the launcher then sent every non-Windows path down the macOS arm, which starts a path as a plain
/// executable. A ".desktop" file is data describing a program, not the program, so after Phase 1 the
/// only paths Linux accepted were paths the launcher could not start, and callers could no longer name
/// the real executable either. No Linux application could be launched at all.
///
/// The tests that should have caught it could not: every non-Windows launch test began by returning
/// early on a Windows host, and this repository is built and tested on Windows. So they passed by not
/// running. Every test in this file runs on EVERY host, because the platform is an argument to the
/// decision rather than something read from the machine, and each one exercises the real handoff -
/// from a desktop entry sitting in a catalogue root, through the catalogue lookup, to the
/// ProcessStartInfo that would start the program.
/// </summary>
public sealed class LinuxDesktopEntryLaunchTests : IDisposable
{
    private readonly string _root;
    private readonly LaunchService _svc = new();

    public LinuxDesktopEntryLaunchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-desktop-entries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteEntry(string fileName, string contents)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private AppCatalog LinuxCatalogue() =>
        new(new[] { (_root, "desktop-entries") }, LaunchPlatform.Linux);

    private const string Firefox = """
        [Desktop Entry]
        Type=Application
        Name=Firefox Web Browser
        Exec=/usr/bin/firefox %u
        Terminal=false
        """;

    // ------------------------------------------- the handoff the inspection said was missing ----

    [Fact]
    public void CatalogueEntryToProcessStart_startsTheProgramTheEntryNames_notTheDesktopFile()
    {
        // The whole finding in one test: a catalogued Linux application, looked up by name exactly as
        // a caller would, and turned into a process start. Before the fix the resolved path WAS the
        // start target, so nothing could ever run.
        var entry = WriteEntry("firefox.desktop", Firefox);

        var (path, error) = LinuxCatalogue().ResolveLaunchPath(null, "firefox");

        Assert.Null(error);
        Assert.Equal(entry, path);

        var psi = _svc.BuildStartInfoFor(new LaunchRequest { Path = path! }, LaunchPlatform.Linux);

        Assert.Equal("/usr/bin/firefox", psi.FileName);
        Assert.NotEqual(entry, psi.FileName);
        Assert.False(psi.UseShellExecute, "a desktop entry's program is spawned directly, never through a shell");
    }

    [Fact]
    public void CatalogueOnLinux_admitsDesktopEntries()
    {
        WriteEntry("firefox.desktop", Firefox);
        WriteEntry("notes.txt", "not an application");

        var found = LinuxCatalogue().Search(null, 50);

        Assert.Contains(found.Apps, a => a.Name == "firefox");
        Assert.DoesNotContain(found.Apps, a => a.Name == "notes");
    }

    [Fact]
    public void CatalogueEntryPath_isAcceptedAsALaunchPath_andStillResolvesToItsProgram()
    {
        // The other way a caller names a target: the catalogued path itself.
        var entry = WriteEntry("firefox.desktop", Firefox);

        var (path, error) = LinuxCatalogue().ResolveLaunchPath(entry, null);

        Assert.Null(error);
        Assert.Equal(entry, path);
        Assert.Equal("/usr/bin/firefox", _svc.BuildStartInfoFor(new LaunchRequest { Path = path! }, LaunchPlatform.Linux).FileName);
    }

    [Fact]
    public void UncataloguedExecutablePath_isStillRefused()
    {
        // The security property Phase 1 established must survive the fix: naming the real executable
        // out of a desktop entry is not a way around the allowlist.
        WriteEntry("firefox.desktop", Firefox);

        var (path, error) = LinuxCatalogue().ResolveLaunchPath("/usr/bin/firefox", null);

        Assert.Null(path);
        Assert.NotNull(error);
        Assert.Contains("catalogue", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the Exec line itself ----

    [Fact]
    public void FieldCodes_areStrippedFromTheStartedCommand()
    {
        var entry = WriteEntry("viewer.desktop", """
            [Desktop Entry]
            Type=Application
            Exec=/usr/bin/viewer --mode=read %F
            """);

        var psi = _svc.BuildStartInfoFor(new LaunchRequest { Path = entry }, LaunchPlatform.Linux);

        Assert.Equal("/usr/bin/viewer", psi.FileName);
        Assert.Equal(new[] { "--mode=read" }, psi.ArgumentList);
    }

    [Fact]
    public void QuotedArgumentsInTheExecLine_stayOneArgument()
    {
        var entry = WriteEntry("titled.desktop", """
            [Desktop Entry]
            Type=Application
            Exec="/opt/my apps/tool" --title "two words" %u
            """);

        var psi = _svc.BuildStartInfoFor(new LaunchRequest { Path = entry }, LaunchPlatform.Linux);

        Assert.Equal("/opt/my apps/tool", psi.FileName);
        Assert.Equal(new[] { "--title", "two words" }, psi.ArgumentList);
    }

    [Fact]
    public void PathKey_becomesTheWorkingDirectory()
    {
        var entry = WriteEntry("worker.desktop", """
            [Desktop Entry]
            Type=Application
            Exec=/usr/bin/worker
            Path=/var/lib/worker
            """);

        var psi = _svc.BuildStartInfoFor(new LaunchRequest { Path = entry }, LaunchPlatform.Linux);

        Assert.Equal("/var/lib/worker", psi.WorkingDirectory);
    }

    [Fact]
    public void OnlyTheDesktopEntryGroupIsRead_notADesktopActionGroup()
    {
        // An entry may carry extra action groups describing OTHER programs. Reading one of those
        // would start something the caller did not name.
        var entry = WriteEntry("multi.desktop", """
            [Desktop Entry]
            Type=Application
            Exec=/usr/bin/real-program

            [Desktop Action NewWindow]
            Name=New Window
            Exec=/usr/bin/something-else --new-window
            """);

        var psi = _svc.BuildStartInfoFor(new LaunchRequest { Path = entry }, LaunchPlatform.Linux);

        Assert.Equal("/usr/bin/real-program", psi.FileName);
    }

    [Fact]
    public void SelfHostArguments_areAppendedAsSeparateArgumentListEntries()
    {
        // Self-host callers still pass arguments (a hosted caller cannot - the Gateway refuses those
        // before any launcher sees them). They must arrive as separate argument-list entries, never
        // concatenated into a command string where a space could become a new word.
        var entry = WriteEntry("firefox.desktop", Firefox);

        var psi = _svc.BuildStartInfoFor(
            new LaunchRequest { Path = entry, Args = "--profile \"my profile\"" }, LaunchPlatform.Linux);

        Assert.Equal("/usr/bin/firefox", psi.FileName);
        Assert.Equal(new[] { "--profile", "my profile" }, psi.ArgumentList);
        Assert.Equal("", psi.Arguments);
    }

    // ------------------------------------------------------- entries that must be refused ----

    [Fact]
    public void ALinkTypeEntry_isRefusedExplicitly()
    {
        var entry = WriteEntry("bookmark.desktop", """
            [Desktop Entry]
            Type=Link
            Name=A bookmark
            URL=https://example.com
            """);

        var ex = Assert.Throws<NotSupportedException>(
            () => _svc.BuildStartInfoFor(new LaunchRequest { Path = entry }, LaunchPlatform.Linux));
        Assert.Contains("Type=Application", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATerminalTrueEntry_isRefusedExplicitlyRatherThanGuessingAnEmulator()
    {
        var entry = WriteEntry("editor.desktop", """
            [Desktop Entry]
            Type=Application
            Exec=/usr/bin/vim
            Terminal=true
            """);

        var ex = Assert.Throws<NotSupportedException>(
            () => _svc.BuildStartInfoFor(new LaunchRequest { Path = entry }, LaunchPlatform.Linux));
        Assert.Contains("terminal emulator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryWithNoExecLine_isRefused()
    {
        var entry = WriteEntry("empty.desktop", """
            [Desktop Entry]
            Type=Application
            Name=Nothing to run
            """);

        Assert.Throws<InvalidOperationException>(
            () => _svc.BuildStartInfoFor(new LaunchRequest { Path = entry }, LaunchPlatform.Linux));
    }

    // --------------------------------------------- the rest of the Linux arm, unchanged ----

    [Fact]
    public void APlainExecutableOnLinux_isStillSpawnedDirectly()
    {
        // The capability that existed before the allowlist and must not be lost: a real program path.
        var exe = Path.Combine(_root, "tool");
        File.WriteAllText(exe, "#!/bin/sh\n");

        var psi = _svc.BuildStartInfoFor(new LaunchRequest { Path = exe, Args = "--foo bar" }, LaunchPlatform.Linux);

        Assert.Equal(exe, psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(new[] { "--foo", "bar" }, psi.ArgumentList);
    }

    [Fact]
    public void AShellScriptOnLinux_isRoutedThroughBash()
    {
        var script = Path.Combine(_root, "run.sh");
        File.WriteAllText(script, "#!/bin/bash\n");

        var psi = _svc.BuildStartInfoFor(new LaunchRequest { Path = script, Args = "one two" }, LaunchPlatform.Linux);

        Assert.Equal("/bin/bash", psi.FileName);
        Assert.Equal(new[] { script, "one", "two" }, psi.ArgumentList);
    }

    [Fact]
    public void AWindowsBatchFileOnLinux_isRefused()
    {
        var bat = Path.Combine(_root, "go.bat");
        File.WriteAllText(bat, "echo hi");

        Assert.Throws<NotSupportedException>(
            () => _svc.BuildStartInfoFor(new LaunchRequest { Path = bat }, LaunchPlatform.Linux));
    }

    // ------------------------------------------------- the Exec parser, on its own terms ----

    [Fact]
    public void ParseExec_doublePercent_isALiteralPercent()
    {
        Assert.Equal(new[] { "/usr/bin/x", "100%" }, DesktopEntry.ParseExec("/usr/bin/x 100%%"));
    }

    [Fact]
    public void ParseExec_unknownFieldCode_isRefusedRatherThanGuessed()
    {
        Assert.Throws<InvalidOperationException>(() => DesktopEntry.ParseExec("/usr/bin/x %z"));
    }

    [Fact]
    public void ParseExec_unterminatedQuote_isRefused()
    {
        Assert.Throws<InvalidOperationException>(() => DesktopEntry.ParseExec("\"/usr/bin/x --flag"));
    }

    [Fact]
    public void ParseExec_escapedQuoteInsideAQuotedArgument_survives()
    {
        Assert.Equal(
            new[] { "/usr/bin/x", "say \"hi\"" },
            DesktopEntry.ParseExec("""/usr/bin/x "say \"hi\"" """));
    }
}
