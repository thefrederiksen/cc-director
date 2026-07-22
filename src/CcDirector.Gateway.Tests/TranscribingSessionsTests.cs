using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway-owned set of sessions whose dictated utterance is being transcribed in the
/// background. It feeds the orange "Transcribing..." roster color. These tests cover the
/// begin/end/idempotency contract and the stale-mark backstop that stops a crashed/offline client
/// from wedging a session orange forever. Every call is scoped to a tenant (issue #1884, Gap B); these
/// single-tenant tests use <see cref="TenantId.Local"/>, the self-host identity. Cross-tenant isolation
/// is proven separately in <see cref="TranscribingSessionsTenantIsolationTests"/>.
/// </summary>
public sealed class TranscribingSessionsTests
{
    private static readonly TenantId T = TenantId.Local;

    [Fact]
    public void IsTranscribing_UnknownSession_IsFalse()
    {
        var store = new TranscribingSessions();

        Assert.False(store.IsTranscribing(T, "sid-1"));
    }

    [Fact]
    public void Begin_ThenIsTranscribing_IsTrue()
    {
        var store = new TranscribingSessions();

        store.Begin(T, "sid-1");

        Assert.True(store.IsTranscribing(T, "sid-1"));
    }

    [Fact]
    public void End_ClearsTheMark()
    {
        var store = new TranscribingSessions();
        store.Begin(T, "sid-1");

        store.End(T, "sid-1");

        Assert.False(store.IsTranscribing(T, "sid-1"));
    }

    [Fact]
    public void Begin_IsIdempotent_AndScopedPerSession()
    {
        var store = new TranscribingSessions();

        store.Begin(T, "sid-1");
        store.Begin(T, "sid-1"); // second Begin must not throw or double-count

        Assert.True(store.IsTranscribing(T, "sid-1"));
        Assert.False(store.IsTranscribing(T, "sid-2")); // another session is untouched
    }

    [Fact]
    public void End_UnknownSession_IsNoOp()
    {
        var store = new TranscribingSessions();

        store.End(T, "never-began"); // must not throw

        Assert.False(store.IsTranscribing(T, "never-began"));
    }

    [Fact]
    public void IsTranscribing_WithinIdleTimeout_StaysTrue()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var store = new TranscribingSessions(() => now);
        store.Begin(T, "sid-1");

        now = now.Add(TranscribingSessions.IdleTimeout); // exactly at the cap is still live

        Assert.True(store.IsTranscribing(T, "sid-1"));
    }

    [Fact]
    public void IsTranscribing_PastIdleTimeout_ExpiresAndClears()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var store = new TranscribingSessions(() => now);
        store.Begin(T, "sid-1");

        now = now.Add(TranscribingSessions.IdleTimeout).AddSeconds(1); // just past the cap

        Assert.False(store.IsTranscribing(T, "sid-1"));
        // The stale mark is removed, so a later read is still false even if the clock rewinds.
        now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(store.IsTranscribing(T, "sid-1"));
    }

    [Fact]
    public void Begin_AfterExpiry_RestartsTheClock()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var store = new TranscribingSessions(() => now);
        store.Begin(T, "sid-1");
        now = now.Add(TranscribingSessions.IdleTimeout).AddMinutes(5);
        Assert.False(store.IsTranscribing(T, "sid-1")); // expired

        store.Begin(T, "sid-1"); // a fresh Send re-marks with the current time

        Assert.True(store.IsTranscribing(T, "sid-1"));
    }

    [Fact]
    public void Refresh_KeepsAnActiveMarkAlivePastTheIdleWindow()
    {
        // A slow upload that streams progress just under the idle window on each step must never be cut
        // short: each Refresh restarts the idle clock, so the mark survives well past IdleTimeout in
        // total as long as progress keeps arriving (issue #1126).
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var store = new TranscribingSessions(() => now);
        store.Begin(T, "sid-1");

        for (var step = 0; step < 5; step++)
        {
            now = now.Add(TranscribingSessions.IdleTimeout).Subtract(TimeSpan.FromSeconds(1)); // just shy of idle
            store.Refresh(T, "sid-1");
        }

        Assert.True(store.IsTranscribing(T, "sid-1")); // still live after 5x the idle window of active upload
    }

    [Fact]
    public void Refresh_DoesNotResurrectAClearedMark()
    {
        var store = new TranscribingSessions();
        store.Begin(T, "sid-1");
        store.End(T, "sid-1");

        store.Refresh(T, "sid-1"); // must not re-mark a session that was already cleared

        Assert.False(store.IsTranscribing(T, "sid-1"));
    }

    [Fact]
    public void Refresh_UnknownSession_IsNoOp()
    {
        var store = new TranscribingSessions();

        store.Refresh(T, "never-began"); // must not throw or create a mark

        Assert.False(store.IsTranscribing(T, "never-began"));
    }
}
