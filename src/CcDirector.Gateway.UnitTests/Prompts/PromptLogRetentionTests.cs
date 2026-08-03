using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Prompts;
using Xunit;

namespace CcDirector.Gateway.Tests.Prompts;

/// <summary>
/// Tests for the prompt log's bounded retention and account erasure (CR-3b, devthrottle_internal
/// issue #1180): the purge that ages daily files out, the delete that removes an account's whole
/// history, the export read behind GET /prompts/export, and the window resolution rules.
/// </summary>
public sealed class PromptLogRetentionTests : IDisposable
{
    private readonly string _dir;
    private readonly GatewayPromptLog _log;

    // A minted account tenant, the exact canonical-lowercase-guid shape DirectoryFor accepts.
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    public PromptLogRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gw-promptret-" + Guid.NewGuid().ToString("N"));
        _log = new GatewayPromptLog(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static PromptRecord Rec(DateTime ts, string text) => new()
    {
        TsUtc = ts,
        Machine = "SOREN_NORTH",
        SessionId = "session-1",
        ContextId = "ctx-1",
        RepoPath = @"D:\ReposFred\devthrottle",
        Agent = "ClaudeCode",
        Role = "user",
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = 1,
        Text = text,
    };

    // ---- PurgeOlderThan: the retention window made true -------------------------------------------

    [Fact]
    public void Purge_removes_an_aged_day_and_keeps_a_young_one_in_every_partition()
    {
        var now = DateTime.UtcNow;
        var aged = now.AddDays(-40);
        _log.Append(TenantId.Local, new[] { Rec(aged, "old local"), Rec(now, "young local") });
        _log.Append(TenantA, new[] { Rec(aged, "old tenant"), Rec(now, "young tenant") });

        var deleted = _log.PurgeOlderThan(now.AddDays(-30));

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(_log.FileFor(TenantId.Local, aged)));
        Assert.False(File.Exists(_log.FileFor(TenantA, aged)));
        Assert.Equal("young local", Assert.Single(_log.Read(TenantId.Local, now, now)).Text);
        Assert.Equal("young tenant", Assert.Single(_log.Read(TenantA, now, now)).Text);
    }

    /// <summary>
    /// The file whose day EQUALS the cutoff's day stays: it holds records younger than the cutoff
    /// instant, and the granularity of this store is the daily file. A record therefore lives at most
    /// one day past the window - never less than the window.
    /// </summary>
    [Fact]
    public void Purge_keeps_the_file_on_the_cutoff_day_itself()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        _log.Append(TenantId.Local, new[] { Rec(cutoff.AddHours(1), "on the boundary day") });

        var deleted = _log.PurgeOlderThan(cutoff);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(_log.FileFor(TenantId.Local, cutoff.AddHours(1))));
    }

    /// <summary>
    /// The purge walks partitions found ON DISK, not a tenant census - so a partition whose tenant was
    /// deleted from the registry still ages out instead of holding that customer's text forever.
    /// </summary>
    [Fact]
    public void Purge_ages_out_a_partition_no_census_would_name()
    {
        var aged = DateTime.UtcNow.AddDays(-40);
        // Simulate an orphaned partition: a tenant folder written in the past whose account is gone.
        // (Written through the log while the tenant "existed" - the folder simply remains on disk.)
        _log.Append(TenantB, new[] { Rec(aged, "orphaned text") });

        var deleted = _log.PurgeOlderThan(DateTime.UtcNow.AddDays(-30));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(_log.FileFor(TenantB, aged)));
    }

    [Fact]
    public void Purge_does_not_touch_files_that_are_not_daily_log_files()
    {
        var stranger = Path.Combine(_dir, "conversation-notes.jsonl");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(stranger, "not a daily file");

        var deleted = _log.PurgeOlderThan(DateTime.UtcNow);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(stranger));
    }

    [Fact]
    public void Purge_of_an_empty_store_is_a_quiet_zero()
    {
        Assert.Equal(0, _log.PurgeOlderThan(DateTime.UtcNow));
    }

    // ---- DeleteAll: the account right-to-erasure ---------------------------------------------------

    [Fact]
    public void DeleteAll_removes_one_tenants_whole_history_and_nobody_elses()
    {
        var now = DateTime.UtcNow;
        _log.Append(TenantA, new[] { Rec(now.AddDays(-5), "a old"), Rec(now, "a new") });
        _log.Append(TenantB, new[] { Rec(now, "b keeps this") });
        _log.Append(TenantId.Local, new[] { Rec(now, "local keeps this") });

        var deleted = _log.DeleteAll(TenantA);

        Assert.Equal(2, deleted);
        Assert.Empty(_log.ReadAll(TenantA));
        Assert.Equal("b keeps this", Assert.Single(_log.ReadAll(TenantB)).Text);
        Assert.Equal("local keeps this", Assert.Single(_log.ReadAll(TenantId.Local)).Text);
    }

    [Fact]
    public void DeleteAll_of_a_tenant_with_no_history_is_a_quiet_zero()
    {
        Assert.Equal(0, _log.DeleteAll(TenantA));
    }

    // ---- ReadAll: the export read ------------------------------------------------------------------

    [Fact]
    public void ReadAll_returns_every_day_oldest_first_without_being_told_a_range()
    {
        var now = DateTime.UtcNow;
        _log.Append(TenantA, new[] { Rec(now, "newest"), Rec(now.AddDays(-400), "over a year old") });

        var all = _log.ReadAll(TenantA);

        Assert.Equal(new[] { "over a year old", "newest" }, all.Select(r => r.Text));
    }

    [Fact]
    public void ReadAll_of_an_absent_partition_is_empty_rather_than_throwing()
    {
        Assert.Empty(_log.ReadAll(TenantA));
    }

    // ---- ResolveRetention: the window's rules ------------------------------------------------------

    [Fact]
    public void Unset_override_means_the_default_everywhere()
    {
        Assert.Equal(PromptLogRetentionSweep.DefaultRetention, PromptLogRetentionSweep.ResolveRetention(isHosted: false, configuredDays: null));
        Assert.Equal(PromptLogRetentionSweep.DefaultRetention, PromptLogRetentionSweep.ResolveRetention(isHosted: true, configuredDays: null));
        Assert.Equal(PromptLogRetentionSweep.DefaultRetention, PromptLogRetentionSweep.ResolveRetention(isHosted: false, configuredDays: "  "));
    }

    [Fact]
    public void A_self_host_operator_may_choose_their_own_window()
    {
        Assert.Equal(TimeSpan.FromDays(365), PromptLogRetentionSweep.ResolveRetention(isHosted: false, configuredDays: "365"));
    }

    /// <summary>
    /// Hosted retention is the PUBLISHED product default; an operator-side environment variable must
    /// never quietly change a promise made to customers, so on hosted a set override is ignored.
    /// </summary>
    [Fact]
    public void The_hosted_gateway_ignores_the_override()
    {
        Assert.Equal(PromptLogRetentionSweep.DefaultRetention, PromptLogRetentionSweep.ResolveRetention(isHosted: true, configuredDays: "365"));
    }

    /// <summary>
    /// A malformed override throws rather than silently running the default: the operator would
    /// believe a window that is not the one running.
    /// </summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("0")]
    [InlineData("-30")]
    public void A_malformed_override_is_refused_loudly(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PromptLogRetentionSweep.ResolveRetention(isHosted: false, configuredDays: configured));
        Assert.Contains(PromptLogRetentionSweep.RetentionDaysEnvVar, ex.Message);
    }

    // ---- The sweep end to end ----------------------------------------------------------------------

    [Fact]
    public void The_sweep_makes_the_window_true()
    {
        var now = DateTime.UtcNow;
        _log.Append(TenantId.Local, new[] { Rec(now.AddDays(-45), "past the window"), Rec(now, "inside the window") });
        var sweep = new PromptLogRetentionSweep(_log, TimeSpan.FromDays(30));

        var deleted = sweep.Sweep();

        Assert.Equal(1, deleted);
        Assert.Equal("inside the window", Assert.Single(_log.ReadAll(TenantId.Local)).Text);
    }

    [Fact]
    public void A_non_positive_window_cannot_be_constructed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PromptLogRetentionSweep(_log, TimeSpan.Zero));
    }
}
