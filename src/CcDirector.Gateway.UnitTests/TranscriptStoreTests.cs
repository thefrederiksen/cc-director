using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The per-tenant dictation transcript store (issue #509): append persists the raw and cleaned text, retention
/// holds each tenant to the window and the cap, and one tenant can never read or trim another's rows. The store
/// takes an EXPLICIT <see cref="TenantId"/> on every call and never infers one, so these drive two distinct
/// tenants over a single SQLite database and prove the isolation the hosted Gateway depends on.
/// </summary>
public sealed class TranscriptStoreTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Base = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ProductionRetention_MatchesSessionHistory_AndTheCapDoesNotUndercutIt()
    {
        // Owner decision, 2026-09-01: every per-tenant record the Gateway keeps ages out on the SAME
        // 90-day clock. Pinning the transcript window TO the history window means neither can be
        // changed alone without this test naming the drift.
        Assert.Equal(CcDirector.Gateway.History.SessionHistorySweep.Retention, TranscriptStore.RetentionWindow);
        Assert.Equal(TimeSpan.FromDays(90), TranscriptStore.RetentionWindow);

        // The cap must not silently undercut the window: the heaviest real tenant measured
        // (2026-09-01) writes ~133 transcripts a day, so 90 days needs ~12,000 rows of headroom.
        // A 10,000 cap would quietly turn "90 days" into ~75 for exactly the tenant the window
        // is most about.
        Assert.True(TranscriptStore.MaxTranscriptsPerTenant >= 133 * 90 * 2,
            $"MaxTranscriptsPerTenant ({TranscriptStore.MaxTranscriptsPerTenant}) leaves less than 2x headroom over the measured heaviest tenant across the retention window");
    }

    [Fact]
    public void Append_PersistsRawAndCleanedAndMetadata()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        store.Append(TenantA, "dictation", rawText: "wave frame", cleanedText: "SwimFrame",
            cleanupApplied: true, turnId: "turn-1", nowUtc: Base);

        var rows = store.List(TenantA);
        Assert.Single(rows);
        var r = rows[0];
        Assert.Equal("wave frame", r.RawText);
        Assert.Equal("SwimFrame", r.CleanedText);
        Assert.Equal("dictation", r.Source);
        Assert.Equal("turn-1", r.TurnId);
        Assert.True(r.CleanupApplied);
        Assert.Equal(TenantA.Value, r.TenantId);
    }

    [Fact]
    public void Append_NullCleaned_DefaultsToRaw()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        store.Append(TenantA, "voice", rawText: "hello world", cleanedText: null,
            cleanupApplied: false, turnId: null, nowUtc: Base);

        var r = Assert.Single(store.List(TenantA));
        Assert.Equal("hello world", r.RawText);
        Assert.Equal("hello world", r.CleanedText);
        Assert.False(r.CleanupApplied);
        Assert.Null(r.TurnId);
    }

    [Fact]
    public void Append_NullRaw_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        Assert.Throws<ArgumentNullException>(() =>
            store.Append(TenantA, "voice", rawText: null!, cleanedText: "x",
                cleanupApplied: false, turnId: null, nowUtc: Base));
    }

    [Fact]
    public void Append_InvalidTenant_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        // A default(TenantId) is not valid - it must never reach a query as an ambient fallback.
        Assert.Throws<ArgumentException>(() =>
            store.Append(default, "voice", rawText: "x", cleanedText: "x",
                cleanupApplied: false, turnId: null, nowUtc: Base));
    }

    [Fact]
    public void OneTenant_CannotSeeAnothersRows()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        store.Append(TenantA, "dictation", "tenant A words", "tenant A words", false, null, Base);
        store.Append(TenantB, "dictation", "tenant B words", "tenant B words", false, null, Base);

        var a = store.List(TenantA);
        Assert.Single(a);
        Assert.Equal("tenant A words", a[0].RawText);
        Assert.Equal(1, store.Count(TenantA));

        var b = store.List(TenantB);
        Assert.Single(b);
        Assert.Equal("tenant B words", b[0].RawText);
        Assert.Equal(1, store.Count(TenantB));
    }

    [Fact]
    public void List_ReturnsNewestFirst()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        store.Append(TenantA, "voice", "oldest", "oldest", false, null, Base);
        store.Append(TenantA, "voice", "middle", "middle", false, null, Base.AddMinutes(1));
        store.Append(TenantA, "voice", "newest", "newest", false, null, Base.AddMinutes(2));

        var rows = store.List(TenantA);
        Assert.Equal(new[] { "newest", "middle", "oldest" }, rows.Select(r => r.RawText).ToArray());
    }

    [Fact]
    public void Retain_AgeTrim_DeletesOnlyRowsPastTheWindow()
    {
        using var h = new GatewayDbTestHarness();
        // 30-day window, generous cap so only AGE bites here.
        var store = new TranscriptStore(h.Open(), TimeSpan.FromDays(30), maxPerTenant: 10_000);

        store.Append(TenantA, "voice", "stale", "stale", false, null, Base);              // t = Base
        store.Append(TenantA, "voice", "fresh", "fresh", false, null, Base.AddDays(40));   // t = Base + 40d

        // Retain as of Base + 40 days: the row at Base is 40 days old (> 30) and goes; the fresh one stays.
        store.Retain(TenantA, Base.AddDays(40));

        var rows = store.List(TenantA);
        Assert.Single(rows);
        Assert.Equal("fresh", rows[0].RawText);
    }

    [Fact]
    public void Retain_CountTrim_KeepsOnlyTheNewestCap()
    {
        using var h = new GatewayDbTestHarness();
        // Small cap so count-trim is exercised without inserting the production 10,000 rows.
        var store = new TranscriptStore(h.Open(), TimeSpan.FromDays(365), maxPerTenant: 5);

        for (var i = 0; i < 8; i++)
            store.Append(TenantA, "voice", $"utterance-{i}", $"utterance-{i}", false, null, Base.AddMinutes(i));

        store.Retain(TenantA, Base.AddMinutes(8));

        var rows = store.List(TenantA);
        Assert.Equal(5, rows.Count);
        // The newest 5 survive (utterances 3..7); the oldest 3 (0..2) are trimmed.
        Assert.Equal(
            new[] { "utterance-7", "utterance-6", "utterance-5", "utterance-4", "utterance-3" },
            rows.Select(r => r.RawText).ToArray());
    }

    [Fact]
    public void Retain_OneTenant_DoesNotTrimAnother()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open(), TimeSpan.FromDays(30), maxPerTenant: 10_000);

        // Both tenants hold a row old enough to be age-trimmed.
        store.Append(TenantA, "voice", "a-stale", "a-stale", false, null, Base);
        store.Append(TenantB, "voice", "b-stale", "b-stale", false, null, Base);

        // Trim ONLY tenant A. The global query filter scopes the delete to A, so B is untouched.
        store.Retain(TenantA, Base.AddDays(40));

        Assert.Equal(0, store.Count(TenantA));
        Assert.Equal(1, store.Count(TenantB));
    }

    [Fact]
    public void Rows_SurviveRestart()
    {
        using var h = new GatewayDbTestHarness();
        new TranscriptStore(h.Open()).Append(TenantA, "dictation", "durable", "durable", false, "t9", Base);

        // Reopen the same file - a simulated Gateway restart.
        var reopened = new TranscriptStore(h.Open());
        var r = Assert.Single(reopened.List(TenantA));
        Assert.Equal("durable", r.RawText);
        Assert.Equal("t9", r.TurnId);
    }
}
