using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The daily-email decision: whether this tenant's daily report carries a dictionary-suggestions block, why,
/// and the once-per-batch cadence that keeps the email from nagging.
///
/// The settings store is REAL, on an isolated SQLite database, because the cadence has to survive a restart
/// and an in-memory double would prove nothing about that. The suggestions are handed in as a list, which is
/// exactly what the composer takes - it reads the stored scan and never triggers one.
///
/// The four answers and their directions:
///   * setting off  -> quiet, and nothing else is even consulted (the user's choice wins first).
///   * no pending   -> quiet, no block (the report simply does not have the section).
///   * pending      -> included, twice, then quiet - and a NEW batch earns its own two.
///   * previewing   -> never spends a mention, so a preview cannot silence a real send.
/// </summary>
public sealed class SuggestionEmailComposerTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Base = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _dbh = new();

    public void Dispose() => _dbh.Dispose();

    private static MistranscriptionSuggestion Sug(string term, params (string heard, int count)[] v)
        => new(term, v.Select(x => new MistranscriptionVariant(x.heard, x.count)).ToList(),
               v.Sum(x => x.count), v.Sum(x => x.count) * 2);

    private static readonly MistranscriptionSuggestion Mindzie =
        Sug("mindzie", ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12));
    private static readonly MistranscriptionSuggestion Frederiksen =
        Sug("Frederiksen", ("Fredriksson", 18), ("Fredrickson", 12));

    private sealed record Rig(SuggestionEmailComposer Composer, TenantSettingsResolver Settings);

    /// <param name="pending">What each tenant has waiting. A tenant absent from the map has nothing.</param>
    private Rig Build(
        Dictionary<string, IReadOnlyList<MistranscriptionSuggestion>> pending,
        string? baseUrl = "https://gw.example.com")
    {
        var settings = new TenantSettingsResolver(new TenantSettingsStore(_dbh.Open()));
        var composer = new SuggestionEmailComposer(
            t => pending.TryGetValue(t.Value, out var list) ? list : Array.Empty<MistranscriptionSuggestion>(),
            settings, () => baseUrl, () => Base);
        return new Rig(composer, settings);
    }

    /// <summary>Both tenants have the same one term waiting, so a difference in outcome can only come from
    /// the per-tenant setting or cadence - never from one of them having nothing to say.</summary>
    private Rig BothTenantsPending()
        => Build(new Dictionary<string, IReadOnlyList<MistranscriptionSuggestion>>
        {
            [TenantA.Value] = new[] { Mindzie },
            [TenantB.Value] = new[] { Mindzie },
        });

    private Rig BuildWith(params MistranscriptionSuggestion[] forTenantA)
        => Build(new Dictionary<string, IReadOnlyList<MistranscriptionSuggestion>>
        {
            [TenantA.Value] = forTenantA,
        });

    // ---- the four reasons -----------------------------------------------------------------------------

    /// <summary>The user's choice wins, and wins FIRST. With the setting off the block is withheld even though
    /// there is a perfectly good batch waiting - which is the only way to tell this apart from "nothing to
    /// say".</summary>
    [Fact]
    public void SettingOff_WithholdsTheBlockEvenWhenTermsArePending()
    {
        var rig = BuildWith(Mindzie); // there IS something to say
        rig.Settings.SetSuggestionsInDailyEmail(TenantA, false, Base);

        var d = rig.Composer.Compose(TenantA, markMentioned: true);
        Assert.False(d.Include);
        Assert.Equal(SuggestionEmailComposer.BlockReason.SettingOff, d.Reason);
        Assert.Null(d.Block);
    }

    /// <summary>Nothing pending, nothing said. The block simply is not in the report.</summary>
    [Fact]
    public void NoSuggestions_WithholdsTheBlock()
    {
        var rig = BuildWith(); // nothing pending

        var d = rig.Composer.Compose(TenantA, markMentioned: true);
        Assert.False(d.Include);
        Assert.Equal(SuggestionEmailComposer.BlockReason.NoSuggestions, d.Reason);
        Assert.Null(d.Block);
        Assert.Equal(0, d.TermCount);
    }

    /// <summary>The default is ON, so a tenant who has never opened Settings gets the block - the feature has
    /// to work for the people it is meant to help.</summary>
    [Fact]
    public void DefaultOn_IncludesTheBlockWithNoSettingWritten()
    {
        var rig = BuildWith(Mindzie);

        var d = rig.Composer.Compose(TenantA, markMentioned: false);
        Assert.True(d.Include);
        Assert.Equal(SuggestionEmailComposer.BlockReason.Included, d.Reason);
        Assert.NotNull(d.Block);
        Assert.Contains("mindzie", d.Block!.Text, StringComparison.Ordinal);
    }

    // ---- the cadence ----------------------------------------------------------------------------------

    /// <summary>
    /// A batch is mentioned twice and then the email goes quiet. The badge on the Dictionary page is the
    /// durable signal; the email is the doorbell and does not keep ringing.
    /// </summary>
    [Fact]
    public void OneBatch_IsMentionedTwiceThenGoesQuiet()
    {
        var rig = BuildWith(Mindzie);

        var first = rig.Composer.Compose(TenantA, markMentioned: true);
        Assert.True(first.Include);
        Assert.Equal(1, first.Mentions);

        var second = rig.Composer.Compose(TenantA, markMentioned: true);
        Assert.True(second.Include);
        Assert.Equal(2, second.Mentions);
        Assert.Equal(first.Batch, second.Batch);

        var third = rig.Composer.Compose(TenantA, markMentioned: true);
        Assert.False(third.Include);
        Assert.Equal(SuggestionEmailComposer.BlockReason.AlreadyMentioned, third.Reason);
        Assert.Null(third.Block);
        // The count did NOT keep climbing on the refused call - a withheld mention is not a mention.
        Assert.Equal(2, third.Mentions);
    }

    /// <summary>New evidence earns new mentions: a batch that gains a term is a different batch, and its two
    /// mentions start over. Without this, a genuinely new word would be silenced by the previous batch's
    /// spent count.</summary>
    [Fact]
    public void NewEvidence_RestartsTheMentions()
    {
        // The scan result the composer reads is swapped under it, which is what a nightly rescan does.
        var pending = new Dictionary<string, IReadOnlyList<MistranscriptionSuggestion>>
        {
            [TenantA.Value] = new[] { Mindzie },
        };
        var rig = Build(pending);

        rig.Composer.Compose(TenantA, markMentioned: true);
        var second = rig.Composer.Compose(TenantA, markMentioned: true);
        Assert.False(rig.Composer.Compose(TenantA, markMentioned: true).Include); // quiet

        // A second term starts being got wrong.
        pending[TenantA.Value] = new[] { Mindzie, Frederiksen };

        var afterNewEvidence = rig.Composer.Compose(TenantA, markMentioned: true);
        Assert.True(afterNewEvidence.Include);
        Assert.NotEqual(second.Batch, afterNewEvidence.Batch);
        Assert.Equal(1, afterNewEvidence.Mentions);
        Assert.Equal(2, afterNewEvidence.TermCount);
    }

    /// <summary>
    /// PREVIEWING IS FREE. Asking without committing must not spend a mention - otherwise a settings preview,
    /// a dry run, or a retry that never sent would quietly consume the batch's budget and the owner would get
    /// one real email instead of two, or none instead of one.
    /// </summary>
    [Fact]
    public void Preview_DoesNotSpendAMention()
    {
        var rig = BuildWith(Mindzie);

        for (var i = 0; i < 5; i++)
        {
            var preview = rig.Composer.Compose(TenantA, markMentioned: false);
            Assert.True(preview.Include);
            Assert.Equal(0, preview.Mentions);
        }

        // Both real sends are still available.
        Assert.True(rig.Composer.Compose(TenantA, markMentioned: true).Include);
        Assert.True(rig.Composer.Compose(TenantA, markMentioned: true).Include);
        Assert.False(rig.Composer.Compose(TenantA, markMentioned: true).Include);
    }

    /// <summary>The cadence is PER TENANT. One account exhausting its mentions must never silence another's -
    /// the cadence state lives in that account's own settings partition.</summary>
    [Fact]
    public void Cadence_IsPerTenant()
    {
        var rig = BothTenantsPending();

        rig.Composer.Compose(TenantA, markMentioned: true);
        rig.Composer.Compose(TenantA, markMentioned: true);
        Assert.False(rig.Composer.Compose(TenantA, markMentioned: true).Include);

        // B has said nothing yet, so B still gets both of its own.
        Assert.True(rig.Composer.Compose(TenantB, markMentioned: true).Include);
        Assert.True(rig.Composer.Compose(TenantB, markMentioned: true).Include);
        Assert.False(rig.Composer.Compose(TenantB, markMentioned: true).Include);
    }

    /// <summary>And one account's setting never reaches another's: A turning the email off leaves B included.</summary>
    [Fact]
    public void Setting_IsPerTenant()
    {
        var rig = BothTenantsPending();
        rig.Settings.SetSuggestionsInDailyEmail(TenantA, false, Base);

        Assert.False(rig.Composer.Compose(TenantA, markMentioned: false).Include);
        Assert.True(rig.Composer.Compose(TenantB, markMentioned: false).Include);
    }

    /// <summary>The mention is recorded durably, not in memory: a Gateway restart does not hand a batch two
    /// fresh mentions. Proved by building a SECOND composer over the same database, which is what a restart
    /// looks like from the store's point of view.</summary>
    [Fact]
    public void Cadence_SurvivesARestart()
    {
        var rig = BuildWith(Mindzie);
        rig.Composer.Compose(TenantA, markMentioned: true);
        rig.Composer.Compose(TenantA, markMentioned: true);

        // A second composer over the SAME database file - what a restart looks like from the store's side.
        var afterRestart = BuildWith(Mindzie);
        var d = afterRestart.Composer.Compose(TenantA, markMentioned: true);

        Assert.False(d.Include);
        Assert.Equal(SuggestionEmailComposer.BlockReason.AlreadyMentioned, d.Reason);
    }

    /// <summary>
    /// THE LINK MUST BE A ROUTE THAT EXISTS. The Cockpit's router is mounted at the ROOT, so its Dictionary
    /// page is <c>{base}/dictionary</c>. Building the link off the <c>{base}/cockpit</c> surface URL instead
    /// produces <c>{base}/cockpit/dictionary</c>, which the Cockpit matches against its
    /// <c>/cockpit/{sessionId}</c> session-redirect route and resolves to nothing - a link that looks right in
    /// review, passes any "does it contain a URL" check, and dead-ends for the reader. This asserts the exact
    /// path, and asserts the wrong one is ABSENT, because that is the only version of the check that fails.
    /// </summary>
    [Fact]
    public void Block_LinksToTheDictionaryRouteThatActuallyExists()
    {
        var withUrl = BuildWith(Mindzie);
        var linked = withUrl.Composer.Compose(TenantA, markMentioned: false);

        Assert.Contains("https://gw.example.com/dictionary", linked.Block!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/cockpit/dictionary", linked.Block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/cockpit/dictionary", linked.Block.Html, StringComparison.Ordinal);
    }

    /// <summary>A trailing slash on the base must not produce a double slash in the link.</summary>
    [Fact]
    public void Block_LinkSurvivesATrailingSlashOnTheBase()
    {
        var rig = Build(
            new Dictionary<string, IReadOnlyList<MistranscriptionSuggestion>> { [TenantA.Value] = new[] { Mindzie } },
            baseUrl: "https://gw.example.com/");

        var linked = rig.Composer.Compose(TenantA, markMentioned: false);
        Assert.Contains("https://gw.example.com/dictionary", linked.Block!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("//dictionary", linked.Block.Text, StringComparison.Ordinal);
    }

    /// <summary>With no publicly reachable address there is no honest link, so the block names the page instead
    /// - never a localhost address in a message read on a phone.</summary>
    [Fact]
    public void Block_WithNoPublicAddress_HasNoLink()
    {
        var noUrl = Build(
            new Dictionary<string, IReadOnlyList<MistranscriptionSuggestion>> { [TenantB.Value] = new[] { Mindzie } },
            baseUrl: null);

        var unlinked = noUrl.Composer.Compose(TenantB, markMentioned: false);
        Assert.DoesNotContain("http", unlinked.Block!.Text, StringComparison.Ordinal);
    }
}

/// <summary>
/// The cadence state itself (issue #2074) - the small rules the composer above leans on, stated in isolation
/// so a change to them fails here first with a plain message.
/// </summary>
public sealed class DictationEmailCadenceStateTests
{
    private static readonly DateTime Base = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void None_MayMentionAnything()
        => Assert.True(DictationEmailCadenceState.None.MayMention("batch-1"));

    [Fact]
    public void SameBatch_MayBeMentionedUpToTheCap()
    {
        var s = DictationEmailCadenceState.None.Mentioned("batch-1", Base);
        Assert.Equal(1, s.Mentions);
        Assert.True(s.MayMention("batch-1"));

        s = s.Mentioned("batch-1", Base.AddDays(1));
        Assert.Equal(2, s.Mentions);
        Assert.False(s.MayMention("batch-1"));
    }

    [Fact]
    public void DifferentBatch_RestartsTheCount()
    {
        var s = DictationEmailCadenceState.None
            .Mentioned("batch-1", Base)
            .Mentioned("batch-1", Base.AddDays(1));
        Assert.False(s.MayMention("batch-1"));

        Assert.True(s.MayMention("batch-2"));
        var next = s.Mentioned("batch-2", Base.AddDays(2));
        Assert.Equal("batch-2", next.Batch);
        Assert.Equal(1, next.Mentions);
    }

    [Fact]
    public void Mentioned_StampsTheTimeInUtc()
    {
        var local = new DateTime(2026, 7, 24, 8, 0, 0, DateTimeKind.Local);
        var s = DictationEmailCadenceState.None.Mentioned("batch-1", local);

        Assert.NotNull(s.LastMentionUtc);
        Assert.Equal(DateTimeKind.Utc, s.LastMentionUtc!.Value.Kind);
    }
}
