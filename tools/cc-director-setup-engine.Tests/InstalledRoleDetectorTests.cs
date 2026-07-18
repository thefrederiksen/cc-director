using System;
using System.IO;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstalledRoleDetectorTests
{
    private static readonly InstallLayout Layout = new(@"C:\root");
    private static readonly string GatewayExe = Layout.PathFor(ComponentRegistry.Gateway);

    // The pre-rename Gateway name, asserted as a LITERAL to PIN the historical spelling. Deriving it
    // from LegacyAliasesFor (the very method under guard) would let a wrong alias spelling still pass:
    // production and the expectation would drift together. Spelled out here, a misspelled alias in
    // LegacyAliasesFor makes Detect_OnlyLegacyGatewayExePresent_IsGateway go red.
    private static readonly string LegacyGatewayExe = Path.Combine(Layout.GatewayDir, "cc-director-gateway.exe");

    /// <summary>
    /// Build a reader whose file-existence answers we control, with no disk access. The same predicate
    /// drives the current-name probe and the legacy-alias probe, so a test simply names which paths exist.
    /// </summary>
    private static InstalledStateReader ReaderWith(Func<string, bool> fileExists) =>
        new(Layout, fileExists: fileExists, aliasFileExists: fileExists, readVersion: _ => null,
            installed: InstalledManifest.Empty());

    [Fact]
    public void Detect_GatewayExePresent_IsGateway()
    {
        var role = InstalledRoleDetector.Detect(Layout, ReaderWith(path => path == GatewayExe));
        Assert.Equal(InstallRole.Gateway, role);
    }

    [Fact]
    public void Detect_OnlyLegacyGatewayExePresent_IsGateway()
    {
        // An existing host whose Gateway is still the pre-rename cc-director-gateway.exe (the current
        // devthrottle-gateway.exe absent) must be recognised as a Gateway so the update refreshes it
        // instead of misclassifying it as a Workstation and orphaning its Gateway (issue #1821).
        var role = InstalledRoleDetector.Detect(Layout, ReaderWith(path => path == LegacyGatewayExe));
        Assert.Equal(InstallRole.Gateway, role);
    }

    [Fact]
    public void Detect_LegacyGatewayNameIsADirectory_IsWorkstation()
    {
        // Issue #1821 false-positive: the legacy alias is probed FILE-only (File.Exists), so a
        // Workstation that merely has a DIRECTORY named gateway\cc-director-gateway.exe is NOT misread
        // as a Gateway host. The renamed Gateway is only ever a file; the one component that is a
        // directory (the macOS .app bundle) is the Director, never the Gateway. Real filesystem, default
        // reader - this exercises the actual File.Exists vs Directory.Exists distinction. Windows-only:
        // the pre-rename name only ever existed on Windows.
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "cc-d1-" + Guid.NewGuid().ToString("N"));
        var layout = new InstallLayout(root);
        var legacyDir = Path.Combine(layout.GatewayDir, "cc-director-gateway.exe");
        try
        {
            Directory.CreateDirectory(legacyDir); // a DIRECTORY sharing the legacy exe's name
            var role = InstalledRoleDetector.Detect(layout); // default reader = real File/Directory probes
            Assert.Equal(InstallRole.Workstation, role);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_GatewayExeAbsent_IsWorkstation()
    {
        // Director present, no Gateway of any name (neither the current nor the legacy exe): a plain
        // Workstation install.
        var role = InstalledRoleDetector.Detect(
            Layout, ReaderWith(path => path != GatewayExe && path != LegacyGatewayExe));
        Assert.Equal(InstallRole.Workstation, role);
    }

    [Fact]
    public void Detect_NothingInstalled_IsWorkstation()
    {
        var role = InstalledRoleDetector.Detect(Layout, ReaderWith(_ => false));
        Assert.Equal(InstallRole.Workstation, role);
    }
}
