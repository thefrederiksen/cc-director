using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// The GATEWAY-OWNED injected-text setting persisted in config.json under <c>injected_text</c>. Proves
/// it defaults to ours when never set, round-trips the user's text (and survives a fresh read from disk,
/// the across-restart guarantee), preserves other sections, and throws rather than silently defaulting
/// when the stored value is the wrong shape. The Validate rules are the guard the endpoint runs before
/// it writes; they are pure and tested directly. Redirects CC_DIRECTOR_ROOT to a temp dir so the tests
/// read/write an isolated config.json, never the user's real one.
/// </summary>
[Collection("ConfigEnvSerial")]
public sealed class InjectedTextConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public InjectedTextConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-injtext-cfg-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Get_WhenNeverSet_DefaultsToOurs()
    {
        var s = InjectedTextConfig.Get();
        Assert.False(s.UseYours);
        Assert.Null(s.Yours);
        Assert.Equal(InjectedTextSettings.Default, s);
    }

    [Fact]
    public void Set_Yours_PersistsAndSurvivesFreshReadFromDisk()
    {
        InjectedTextConfig.Set(new InjectedTextSettings(UseYours: true, Yours: "my words, [SESSION_ID]"));

        var s = InjectedTextConfig.Get();
        Assert.True(s.UseYours);
        Assert.Equal("my words, [SESSION_ID]", s.Yours);

        // Across-restart guarantee: durable on disk, so a fresh read sees it.
        var onDisk = CcDirectorConfigService.ReadRaw();
        var obj = Assert.IsType<JsonObject>(onDisk[InjectedTextConfig.Key]);
        Assert.True((bool)obj["use_yours"]!);
        Assert.Equal("my words, [SESSION_ID]", (string?)obj["yours"]);
    }

    [Fact]
    public void Set_BackToOurs_KeepsTheUsersTextForLater()
    {
        InjectedTextConfig.Set(new InjectedTextSettings(UseYours: true, Yours: "my own text"));

        // Switching back to ours keeps yours so the user can adopt it again without rewriting it. The
        // Cockpit carries the full state; the store simply persists what it is given.
        InjectedTextConfig.Set(new InjectedTextSettings(UseYours: false, Yours: "my own text"));

        var s = InjectedTextConfig.Get();
        Assert.False(s.UseYours);
        Assert.Equal("my own text", s.Yours);
    }

    [Fact]
    public void Set_PreservesOtherConfigSections()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = "http://gw.example:7878" },
        });

        InjectedTextConfig.Set(new InjectedTextSettings(UseYours: true, Yours: "hello"));

        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal("http://gw.example:7878", (string?)onDisk["gateway"]!["url"]);
    }

    [Fact]
    public void Get_WhenInjectedTextIsNotObject_Throws()
    {
        CcDirectorConfigService.MergePatch(new JsonObject { [InjectedTextConfig.Key] = "nonsense" });
        Assert.Throws<InvalidOperationException>(() => InjectedTextConfig.Get());
    }

    [Fact]
    public void Get_WhenUseYoursIsNotBoolean_Throws()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            [InjectedTextConfig.Key] = new JsonObject { ["use_yours"] = "yes" },
        });
        Assert.Throws<InvalidOperationException>(() => InjectedTextConfig.Get());
    }

    [Fact]
    public void Get_WhenYoursIsNotString_Throws()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            [InjectedTextConfig.Key] = new JsonObject { ["yours"] = 42 },
        });
        Assert.Throws<InvalidOperationException>(() => InjectedTextConfig.Get());
    }

    [Fact]
    public void Ours_IsTheShippedDefault()
        => Assert.Equal(FleetPreambleTemplate.Default, InjectedTextConfig.Ours);

    // ---- Validate (pure) ----

    [Fact]
    public void Validate_AcceptsOurs()
        => Assert.Null(InjectedTextConfig.Validate(InjectedTextSettings.Default));

    [Fact]
    public void Validate_AcceptsAGoodCustomTemplate()
        => Assert.Null(InjectedTextConfig.Validate(new InjectedTextSettings(true, "hi [SESSION_ID]")));

    [Fact]
    public void Validate_RejectsAnUnrenderableTemplate()
    {
        var problem = InjectedTextConfig.Validate(new InjectedTextSettings(true, "[IF_SIGNED_IN]\nhello"));
        Assert.NotNull(problem);
        Assert.Contains("never closed", problem);
    }

    // The user's right to inject nothing at all: empty custom text is a legitimate value.
    [Fact]
    public void Validate_AllowsEmptyCustomText_InjectNothing()
        => Assert.Null(InjectedTextConfig.Validate(new InjectedTextSettings(true, "")));

    // Empty (inject nothing) is a value; absent (chose yours but provided no text) is incoherent.
    [Fact]
    public void Validate_RejectsUseYoursWithNoTextAtAll()
    {
        var problem = InjectedTextConfig.Validate(new InjectedTextSettings(true, null));
        Assert.NotNull(problem);
        Assert.Contains("cannot be absent", problem);
    }

    [Fact]
    public void Set_RejectsAnUnrenderableTemplate()
        => Assert.Throws<ArgumentException>(
            () => InjectedTextConfig.Set(new InjectedTextSettings(true, "[IF_SIGNED_IN]\nhello")));
}
