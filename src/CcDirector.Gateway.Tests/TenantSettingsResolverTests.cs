using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The typed per-tenant resolver (issue #2017): tenant override when set and valid, otherwise the operator
/// global default - and NEVER another tenant's value. These tests assert the resolver's own contract; the
/// global-default arm is asserted by comparing against the same global helper the resolver falls back to (so
/// the test is robust to whatever operator default the environment holds), while the override arm is exact.
/// </summary>
public sealed class TenantSettingsResolverTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
    private const TranscriptionMode Mode = TranscriptionMode.DevThrottle;

    private static TenantSettingsResolver NewResolver(GatewayDbTestHarness h)
        => new(new TenantSettingsStore(h.Open()));

    // ---- the spoken language, and the model it moves ------------------------------------------

    [Fact]
    public void SpokenLanguage_Unset_IsEnglish()
    {
        using var h = new GatewayDbTestHarness();
        Assert.Equal("en", NewResolver(h).SpokenLanguage(TenantA));
    }

    [Fact]
    public void SpokenLanguage_English_ClearsRatherThanStoring()
    {
        // "back to default" and "never chose" must be the same state on disk, so nothing has to
        // interpret a stored "en" differently from an absent value.
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        r.SetSpokenLanguage(TenantA, "da", Now);
        r.SetSpokenLanguage(TenantA, "en", Now);
        Assert.Equal("en", r.SpokenLanguage(TenantA));
    }

    [Fact]
    public void SpokenLanguage_RejectsALanguageWeDoNotOffer()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        Assert.Throws<ArgumentException>(() => r.SetSpokenLanguage(TenantA, "kl", Now));
    }

    [Fact]
    public void SpokenLanguage_IsPerTenant()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        r.SetSpokenLanguage(TenantA, "da", Now);
        Assert.Equal("da", r.SpokenLanguage(TenantA));
        Assert.Equal("en", r.SpokenLanguage(TenantB));
    }

    [Fact]
    public void SpeechBeforeLanguageSwitch_RoundTripsModelAndVoice()
    {
        // This memo is what lets a trip to Danish and back put the account on the engine it started
        // on, instead of stranding it on a costlier multilingual one it no longer needs.
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetSpeechBeforeLanguageSwitch(TenantA, "hexgrad/Kokoro-82M", "af_bella", Now);
        var memo = r.SpeechBeforeLanguageSwitch(TenantA);

        Assert.NotNull(memo);
        Assert.Equal("hexgrad/Kokoro-82M", memo!.Value.Model);
        Assert.Equal("af_bella", memo.Value.Voice);
    }

    [Fact]
    public void SpeechBeforeLanguageSwitch_RemembersAModelThatHadNoVoice()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetSpeechBeforeLanguageSwitch(TenantA, "some/expressive-model", null, Now);
        var memo = r.SpeechBeforeLanguageSwitch(TenantA);

        Assert.NotNull(memo);
        Assert.Null(memo!.Value.Voice);
    }

    [Fact]
    public void SpeechBeforeLanguageSwitch_Unset_IsNull()
    {
        using var h = new GatewayDbTestHarness();
        Assert.Null(NewResolver(h).SpeechBeforeLanguageSwitch(TenantA));
    }

    [Fact]
    public void SpeechBeforeLanguageSwitch_Cleared_IsNull()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        r.SetSpeechBeforeLanguageSwitch(TenantA, "hexgrad/Kokoro-82M", "af_bella", Now);
        r.ClearSpeechBeforeLanguageSwitch(TenantA);
        Assert.Null(r.SpeechBeforeLanguageSwitch(TenantA));
    }

    [Fact]
    public void ClearTtsVoice_RemovesTheOverrideRatherThanStoringABlank()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        r.SetTtsVoice(TenantA, "af_bella", Now);
        r.ClearTtsVoice(TenantA);
        // Falls back to the operator default, which is the honest "no tenant choice" answer.
        Assert.Equal(TtsVoiceConfig.Resolve(Mode), r.TtsVoice(TenantA, Mode));
    }

    [Fact]
    public void CarModeModel_OverrideSet_ReturnsOverride()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetCarModeModel(TenantA, "a-custom-model", Now);

        Assert.Equal("a-custom-model", r.CarModeModel(TenantA));
    }

    [Fact]
    public void CarModeModel_NoOverride_ReturnsOperatorGlobalDefault()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        Assert.Equal(CarModeModelConfig.Resolve(), r.CarModeModel(TenantA));
    }

    [Fact]
    public void OneTenantsOverride_DoesNotLeakToAnother_WhoGetsTheGlobalDefault()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetCarModeModel(TenantA, "a-custom-model", Now);

        // Tenant B never set an override: it must get the OPERATOR global default, never tenant A's value.
        Assert.Equal("a-custom-model", r.CarModeModel(TenantA));
        Assert.Equal(CarModeModelConfig.Resolve(), r.CarModeModel(TenantB));
        Assert.NotEqual("a-custom-model", r.CarModeModel(TenantB));
    }

    [Fact]
    public void WingmanModel_RolesAreStoredSeparately()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetWingmanModel(TenantA, WingmanModelRole.Thinking, "thinker", Now);
        r.SetWingmanModel(TenantA, WingmanModelRole.Fast, "sprinter", Now);

        Assert.Equal("thinker", r.WingmanModel(TenantA, Mode, WingmanModelRole.Thinking));
        Assert.Equal("sprinter", r.WingmanModel(TenantA, Mode, WingmanModelRole.Fast));
    }

    [Fact]
    public void TimeZone_InvalidOverrideStored_FallsBackToGlobalDefault()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        // A corrupt override (e.g. a value that later fails validation) must degrade to the operator default,
        // not crash a turn and not leak another tenant's value. Write an invalid id straight through the store.
        store.Set(TenantA, TenantSettingKeys.TimeZone, "Not/AZone", Now);

        Assert.Equal(TimeZoneConfig.Get(), r.TimeZone(TenantA));
    }

    [Fact]
    public void TimeZone_ValidOverride_ReturnsOverride()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetTimeZone(TenantA, "Asia/Tokyo", Now);

        Assert.Equal("Asia/Tokyo", r.TimeZone(TenantA));
    }

    [Fact]
    public void SetTimeZone_InvalidId_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        Assert.Throws<ArgumentException>(() => r.SetTimeZone(TenantA, "Not/AZone", Now));
    }

    [Fact]
    public void SetCarModeModel_Empty_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        Assert.Throws<ArgumentException>(() => r.SetCarModeModel(TenantA, "   ", Now));
    }

    [Fact]
    public void SnoozePresets_ValidSet_RoundTripsAndDefaultPersists()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetSnoozePresets(TenantA, new[] { 240, 15, 60 }, 60, Now);

        Assert.Equal(new[] { 15, 60, 240 }, r.SnoozePresets(TenantA)); // sorted ascending
        Assert.Equal(60, r.SnoozeDefaultMinutes(TenantA));
    }

    [Fact]
    public void SnoozePresets_NoOverride_ReturnsGlobalDefaults()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        Assert.Equal(SnoozePresetsConfig.Get(), r.SnoozePresets(TenantA));
        Assert.Equal(SnoozeDefaultConfig.Get(), r.SnoozeDefaultMinutes(TenantA));
    }

    [Fact]
    public void SnoozeDefault_CorruptOverride_FallsBackToGlobalDefault()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        store.Set(TenantA, TenantSettingKeys.SnoozeDefaultMinutes, "not-a-number", Now);

        Assert.Equal(SnoozeDefaultConfig.Get(), r.SnoozeDefaultMinutes(TenantA));
    }

    [Fact]
    public void SetSnoozePresets_DefaultNotInList_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        // The default must be one of the presets - the same invariant the global setter enforces.
        Assert.Throws<ArgumentException>(() => r.SetSnoozePresets(TenantA, new[] { 15, 60 }, 999, Now));
    }
}
