using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The store decides WHOSE text reaches the agent. These tests are about that decision, and about the
/// one rule that matters most: when the user has declined our text, no failure path may quietly put
/// it back.
///
/// The choice of whose text is live is held in memory here, NOT read from the real config.json. A test
/// that read the developer's own setting would pass or fail based on how that developer happens to
/// have their machine configured, which is a coin toss wearing a test's clothes.
/// </summary>
public sealed class InjectedTextStoreTests : IDisposable
{
    private readonly string _dir;
    private bool _useYours;

    public InjectedTextStoreTests()
        => _dir = Path.Combine(Path.GetTempPath(), "cc-injected-text-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private InjectedTextStore NewStore()
        => new(_dir, () => new InjectedTextConfig(_useYours), v => _useYours = v);

    // BuildForSession collapses whitespace-only text to nothing, which is correct for a user who cleared
    // their version - but it means a shipped default that ever rendered to whitespace would silently
    // inject NOTHING into every agent, everywhere, with no error. That cannot happen today and this test
    // is why it stays that way: the assumption is load-bearing, so it is asserted rather than trusted.
    [Fact]
    public void OurDefault_NeverRendersToNothing()
    {
        var dir = Path.Combine(_dir, "always-ours");

        var text = FleetPreamble.BuildForSession(
            "a3dfb85e-49dd-442a-9e36-40fc44838783", "devthrottle", "MACHINE_A", @"C:\repos\devthrottle",
            user: null, store: InjectedTextStore.AlwaysOurs(dir));

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("cc-devthrottle", text);
    }

    [Fact]
    public void EnsureOursWritten_PutsTheShippedTextOnDisk()
    {
        var store = NewStore();

        store.EnsureOursWritten();

        Assert.True(File.Exists(store.OursPath));
        Assert.Equal(FleetPreambleTemplate.Default, File.ReadAllText(store.OursPath));
    }

    // The owner's requirement: our updates are always there, even for someone running their own text,
    // so they can read the current default and adopt it.
    [Fact]
    public void EnsureOursWritten_RefreshesOurText_EvenWhileTheUserIsRunningTheirOwn()
    {
        var store = NewStore();
        store.SaveYours("my own text");
        File.WriteAllText(store.OursPath, "a stale default from an older version");

        store.EnsureOursWritten();

        Assert.Equal(FleetPreambleTemplate.Default, File.ReadAllText(store.OursPath));
        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());
        Assert.Equal("my own text", store.ActiveTemplate());
    }

    // ours.txt is a COPY for the user to read, never the source. Hand-editing it must not change what
    // gets injected, or the Settings tab would show one thing and sessions would launch with another.
    [Fact]
    public void OursOnDisk_IsACopy_HandEditsDoNotChangeWhatIsInjected()
    {
        var store = NewStore();
        store.EnsureOursWritten();

        File.WriteAllText(store.OursPath, "somebody edited this file by hand");

        Assert.Equal(FleetPreambleTemplate.Default, store.ActiveTemplate());
    }

    [Fact]
    public void AFreshInstall_RunsOurText()
    {
        _useYours = false;
        var store = NewStore();

        Assert.Equal(InjectedTextSource.Ours, store.ActiveSource());
        Assert.Equal(FleetPreambleTemplate.Default, store.ActiveTemplate());
        Assert.False(store.HasYours);
    }

    [Fact]
    public void SaveYours_MakesTheirTextLive()
    {
        var store = NewStore();

        store.SaveYours("just my words, [SESSION_ID]");

        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());
        Assert.Equal("just my words, [SESSION_ID]", store.ActiveTemplate());
    }

    // Yours or ours - never a merge, and never a mix. What is live is exactly one of the two.
    [Fact]
    public void SwitchingBackToOurs_KeepsTheirTextOnDisk()
    {
        var store = NewStore();
        store.SaveYours("my own text");

        store.UseOurs();

        Assert.Equal(InjectedTextSource.Ours, store.ActiveSource());
        Assert.Equal(FleetPreambleTemplate.Default, store.ActiveTemplate());
        // Their words survive: switching back to ours to compare must never destroy their writing.
        Assert.True(store.HasYours);
        Assert.Equal("my own text", store.ReadYours());

        store.UseYours();
        Assert.Equal("my own text", store.ActiveTemplate());
    }

    // THE RULE THIS FEATURE EXISTS FOR. The user turned our text off. If their file cannot be read we
    // must NOT hand the agent ours instead - that would silently inject the policy they declined,
    // which is the exact thing they opted out of. It fails loudly instead.
    [Fact]
    public void TheirTextLiveButUnreadable_FailsLoudly_AndNeverSubstitutesOurs()
    {
        var store = NewStore();
        store.SaveYours("my own text");

        // Their live text disappears from under them - a sync, a cleanup, a disk error.
        File.Delete(store.YoursPath);

        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());
        var ex = Assert.Throws<InjectedTextUnavailableException>(() => store.ActiveTemplate());

        // The message must not carry our text, and must say plainly that we did NOT swap ours in.
        Assert.DoesNotContain("NEVER SIGN IT", ex.Message);
        Assert.Contains("you turned that off", ex.Message);
        Assert.Contains("Injected text", ex.Message);
    }

    // THE SAME RULE, AT THE OTHER END. This test previously asserted the OPPOSITE - that choosing
    // theirs with no file on disk quietly left our text live. That was a defect being defended by a
    // test: it is indistinguishable from the case above where a live custom file is deleted, and in
    // that case it silently resumes injecting our text, and our policy, into someone who declined it.
    // Chosen-but-absent is a loud failure, not a quiet reversal.
    [Fact]
    public void TheirTextChosenButNeverWritten_FailsLoudly_AndNeverSubstitutesOurs()
    {
        _useYours = true;
        var store = NewStore();

        Assert.False(store.HasYours);
        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());

        var ex = Assert.Throws<InjectedTextUnavailableException>(() => store.ActiveTemplate());
        Assert.DoesNotContain("NEVER SIGN IT", ex.Message);
    }

    [Fact]
    public void UseYours_WithNothingWritten_IsRejectedInPlainEnglish()
    {
        var store = NewStore();

        var ex = Assert.Throws<InvalidOperationException>(() => store.UseYours());

        Assert.Contains("Write one first", ex.Message);
    }

    // The failure lands on the person editing, not on seven agents at launch.
    [Fact]
    public void SaveYours_RejectsATemplateThatCannotRender()
    {
        var store = NewStore();

        var ex = Assert.Throws<FleetPreambleTemplateException>(
            () => store.SaveYours("[IF_SIGNED_IN]\nhello"));

        Assert.Contains("never closed", ex.Message);
        Assert.False(store.HasYours);
    }

    // The user is allowed to throw all of it away, including the fleet commands and our policy text.
    // That is their right and the whole point; it must not be quietly refused.
    [Fact]
    public void TheUserMayInjectNothingAtAll()
    {
        var store = NewStore();

        store.SaveYours("");

        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());
        Assert.Equal("", store.ActiveTemplate());
    }
}
