using System.Text.Json;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Editable/versioned wingman instructions (issue #537): the wingman uses the active version
/// (custom else deployed default); a non-customized user auto-tracks the latest default, while a
/// customized user is told when the dev team ships a new default and can switch to it.
///
/// Over the EF data layer (Hosted Gateway mission, Step 1b): the whole state document is one row per tenant
/// in wingman_instructions. A "reload" is a new store over the same database.
/// </summary>
public sealed class WingmanInstructionsStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    // A fixed legacy path per test - it never exists (so no import) except where a test seeds it explicitly.
    private readonly string _legacyPath;

    public WingmanInstructionsStoreTests()
        => _legacyPath = _h.LegacyPath("wmi-legacy-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() => _h.Dispose();

    private WingmanInstructionsStore New(string def, string ver = "1")
        => new(_h.Open(), _legacyPath, defaultContent: def, defaultVersion: ver);

    [Fact]
    public void Fresh_UsesDeployedDefault_NotCustomized_NoUpdate()
    {
        var s = New("DEFAULT v1");
        Assert.False(s.IsCustomized);
        Assert.Equal("DEFAULT v1", s.ActiveContent);
        Assert.False(s.UpdateAvailable);
    }

    [Fact]
    public void Save_BecomesActiveAndCustomized()
    {
        var s = New("DEFAULT v1");
        var v = s.Save("MY custom instructions", "first try");
        Assert.True(s.IsCustomized);
        Assert.Equal("MY custom instructions", s.ActiveContent);
        Assert.Equal("user", v.Source);
        Assert.Single(s.Versions());
        Assert.False(s.UpdateAvailable);   // editing acknowledges the current default
    }

    [Fact]
    public void Save_EmptyOrOversized_Throws()
    {
        var s = New("DEFAULT v1");
        Assert.Throws<ArgumentException>(() => s.Save("   ", null));
        Assert.Throws<ArgumentException>(() => s.Save(new string('x', WingmanInstructionsStore.MaxContentChars + 1), null));
    }

    [Fact]
    public void CustomizedUser_NewDefaultShips_UpdateAvailable_WithOldDefaultForDiff()
    {
        New("DEFAULT v1").Save("MY custom", null);          // customize against v1
        var s2 = New("DEFAULT v2 changed", "2");            // dev team ships a new default (same database)
        Assert.True(s2.IsCustomized);
        Assert.Equal("MY custom", s2.ActiveContent);        // still on the user's version
        Assert.True(s2.UpdateAvailable);                    // but told a new default exists
        var (ackVer, ackContent) = s2.AcknowledgedDefault();
        Assert.Equal("DEFAULT v1", ackContent);             // the diff's left side = what they based on
    }

    [Fact]
    public void NonCustomizedUser_NewDefaultShips_AutoTracks_NoBanner()
    {
        New("DEFAULT v1");                                  // never customized
        var s2 = New("DEFAULT v2 changed", "2");
        Assert.False(s2.IsCustomized);
        Assert.Equal("DEFAULT v2 changed", s2.ActiveContent);   // rides the latest default
        Assert.False(s2.UpdateAvailable);                       // no stale banner
    }

    [Fact]
    public void SwitchToDefault_AdoptsLatest_ClearsUpdate()
    {
        New("DEFAULT v1").Save("MY custom", null);
        var s2 = New("DEFAULT v2 changed", "2");
        Assert.True(s2.UpdateAvailable);
        s2.SwitchToDefault();
        Assert.False(s2.IsCustomized);
        Assert.Equal("DEFAULT v2 changed", s2.ActiveContent);
        Assert.False(s2.UpdateAvailable);
    }

    [Fact]
    public void Revert_MakesAnOlderVersionActiveAgain()
    {
        var s = New("DEFAULT v1");
        var v1 = s.Save("version one", "v1");
        s.Save("version two", "v2");
        Assert.Equal("version two", s.ActiveContent);
        Assert.True(s.Revert(v1.Id));
        Assert.Equal("version one", s.ActiveContent);
        Assert.False(s.Revert("does-not-exist"));
    }

    [Fact]
    public void State_PersistsAcrossReload()
    {
        New("DEFAULT v1").Save("persisted custom", "keep");
        var s2 = New("DEFAULT v1");                          // reload from the same database
        Assert.True(s2.IsCustomized);
        Assert.Equal("persisted custom", s2.ActiveContent);
        Assert.Single(s2.Versions());
    }

    [Fact]
    public void LegacyJson_ImportedOnce_ActiveContentPinsTheSameVersion_ThenRenamedAside()
    {
        // A legacy wingman-instructions.json written by the old store: a state document with TWO saved
        // versions and the active pointer aimed at the second. After the migration, ActiveContent must
        // resolve to exactly the version the pointer names - not the latest, not the default.
        var older = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc);
        WriteLegacyState(_legacyPath, activeVersionId: "v-active", ackVersion: "1", ackContent: "DEFAULT v1",
            versions: new[]
            {
                LegacyVersion("v-old", "old custom content", older, "old", "user", WingmanInstructionsStore.Hash("old custom content")),
                LegacyVersion("v-active", "active custom content", newer, "active", "user", WingmanInstructionsStore.Hash("active custom content")),
            });

        var s = new WingmanInstructionsStore(_h.Open(), _legacyPath, defaultContent: "DEFAULT v1", defaultVersion: "1");

        // The active pointer is honoured exactly.
        Assert.True(s.IsCustomized);
        Assert.Equal("active custom content", s.ActiveContent);
        Assert.Equal("v-active", s.Active().Id);
        // Both versions survived, newest-first.
        var versions = s.Versions();
        Assert.Equal(2, versions.Count);
        Assert.Equal("v-active", versions[0].Id);
        Assert.Equal("v-old", versions[1].Id);

        // The legacy file is renamed aside and not re-imported.
        Assert.False(File.Exists(_legacyPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_legacyPath)!, Path.GetFileName(_legacyPath) + ".migrated-*"));

        // A fresh store over the same database resolves the same active version (no re-import).
        var reloaded = new WingmanInstructionsStore(_h.Open(), _legacyPath, defaultContent: "DEFAULT v1", defaultVersion: "1");
        Assert.Equal("active custom content", reloaded.ActiveContent);
    }

    [Fact]
    public void LegacyJson_Imported_AckDefaultStateRoundTrips_DrivingTheUpdateBanner()
    {
        // The acknowledged-default snapshot (what the user based their customization on) must survive the
        // import exactly, because the update-available banner is computed from it. The user customized
        // against "DEFAULT v1"; the dev team has since shipped "DEFAULT v2 changed".
        var when = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        WriteLegacyState(_legacyPath, activeVersionId: "v1", ackVersion: "1", ackContent: "DEFAULT v1",
            versions: new[] { LegacyVersion("v1", "my custom", when, null, "user", WingmanInstructionsStore.Hash("my custom")) });

        var s = new WingmanInstructionsStore(_h.Open(), _legacyPath, defaultContent: "DEFAULT v2 changed", defaultVersion: "2");

        // The acknowledged default round-tripped exactly.
        var (ackVersion, ackContent) = s.AcknowledgedDefault();
        Assert.Equal("1", ackVersion);
        Assert.Equal("DEFAULT v1", ackContent);
        // And it drives the banner: customized, and the acknowledged default differs from the new one.
        Assert.True(s.IsCustomized);
        Assert.True(s.UpdateAvailable);
        Assert.Equal("my custom", s.ActiveContent);
    }

    [Fact]
    public void CorruptLegacyJson_FailsLoud_AndLeavesTheFileInPlace()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_legacyPath)!);
        const string corrupt = "{ this is not json !!!";
        File.WriteAllText(_legacyPath, corrupt);

        Assert.Throws<InvalidOperationException>(() =>
            new WingmanInstructionsStore(_h.Open(), _legacyPath, defaultContent: "DEFAULT v1", defaultVersion: "1"));

        Assert.True(File.Exists(_legacyPath));
        Assert.Equal(corrupt, File.ReadAllText(_legacyPath));
    }

    // The legacy StateFile JSON shape, written with default (PascalCase) options exactly as the old store did.
    private static object LegacyVersion(string id, string content, DateTime createdAtUtc, string? label, string source, string hash)
        => new { Id = id, Content = content, CreatedAtUtc = createdAtUtc, Label = label, Source = source, Hash = hash };

    private static void WriteLegacyState(string path, string? activeVersionId, string ackVersion, string ackContent, object[] versions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var state = new
        {
            ActiveVersionId = activeVersionId,
            AckDefaultVersion = ackVersion,
            AckDefaultContent = ackContent,
            Versions = versions,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
