using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Prompts;
using Xunit;

namespace CcDirector.Gateway.Tests.Prompts;

/// <summary>
/// Tests for <see cref="GatewayPromptLog"/> (issue #1551) - THE prompt log. The Director keeps no copy,
/// so what this stores is the only record there is.
/// </summary>
public sealed class GatewayPromptLogTests : IDisposable
{
    private readonly string _dir;
    private readonly GatewayPromptLog _log;

    public GatewayPromptLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gw-promptlog-" + Guid.NewGuid().ToString("N"));
        _log = new GatewayPromptLog(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static PromptRecord Rec(DateTime ts, string text, string role = "user", string? machine = "SOREN_NORTH") => new()
    {
        TsUtc = ts,
        Machine = machine,
        SessionId = "session-1",
        ContextId = "ctx-1",
        RepoPath = @"D:\ReposFred\devthrottle",
        Agent = "ClaudeCode",
        Role = role,
        Modality = role == "user" ? "typed" : null,
        Surface = role == "user" ? "desktop" : null,
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
        Text = text,
    };

    [Fact]
    public void Append_then_Read_round_trips_a_pushed_message()
    {
        var ts = new DateTime(2026, 7, 14, 9, 30, 0, DateTimeKind.Utc);

        var written = _log.Append(new[] { Rec(ts, "fix the login bug") });

        Assert.Equal(1, written);
        var only = Assert.Single(_log.Read(ts, ts));
        Assert.Equal("fix the login bug", only.Text);
        Assert.Equal("SOREN_NORTH", only.Machine);
        Assert.Equal("ctx-1", only.ContextId);
        Assert.Equal("typed", only.Modality);
        Assert.Equal("desktop", only.Surface);
    }

    [Fact]
    public void Append_adds_to_what_is_already_there()
    {
        var ts = new DateTime(2026, 7, 14, 9, 30, 0, DateTimeKind.Utc);
        _log.Append(new[] { Rec(ts, "first") });
        _log.Append(new[] { Rec(ts.AddMinutes(1), "second", "assistant") });

        Assert.Equal(new[] { "first", "second" }, _log.Read(ts, ts).Select(r => r.Text));
    }

    /// <summary>
    /// A Director pushes one batch that spans days - a backfill of old history, or simply a batch that
    /// crosses midnight. Each message must land in ITS OWN day's file, or a week's reading is wrong.
    /// </summary>
    [Fact]
    public void A_batch_spanning_days_is_split_across_the_right_daily_files()
    {
        var day1 = new DateTime(2026, 7, 14, 23, 59, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 7, 15, 0, 1, 0, DateTimeKind.Utc);

        var written = _log.Append(new[] { Rec(day1, "before midnight"), Rec(day2, "after midnight") });

        Assert.Equal(2, written);
        Assert.Equal("before midnight", Assert.Single(_log.Read(day1, day1)).Text);
        Assert.Equal("after midnight", Assert.Single(_log.Read(day2, day2)).Text);
    }

    [Fact]
    public void Read_spans_days_and_returns_oldest_first()
    {
        var day1 = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        var day3 = new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Utc);
        _log.Append(new[] { Rec(day1, "monday"), Rec(day3, "wednesday") });

        Assert.Equal(new[] { "monday", "wednesday" }, _log.Read(day1, day3).Select(r => r.Text));
    }

    [Fact]
    public void Read_of_a_day_with_nothing_in_it_is_empty_rather_than_throwing()
    {
        var ts = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        Assert.Empty(_log.Read(ts, ts));
    }

    [Fact]
    public void A_corrupt_line_does_not_hide_the_good_ones()
    {
        var ts = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        _log.Append(new[] { Rec(ts, "good one") });
        File.AppendAllText(_log.FileFor(ts), "{ this is not json" + Environment.NewLine);
        _log.Append(new[] { Rec(ts.AddMinutes(1), "good two") });

        Assert.Equal(new[] { "good one", "good two" }, _log.Read(ts, ts).Select(r => r.Text));
    }

    [Fact]
    public void A_multi_line_prompt_survives_as_one_record()
    {
        var ts = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        var text = "line one\nline two\nline three";
        _log.Append(new[] { Rec(ts, text) });

        Assert.Equal(text, Assert.Single(_log.Read(ts, ts)).Text);
    }

    [Fact]
    public void Records_from_two_machines_are_both_kept_and_distinguishable()
    {
        var ts = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);

        // The whole point of the log living on the Gateway: it holds the fleet, not one machine.
        _log.Append(new[] { Rec(ts, "from north", machine: "SOREN_NORTH") });
        _log.Append(new[] { Rec(ts.AddMinutes(1), "from laptop", machine: "SOREN_LAPTOP") });

        var records = _log.Read(ts, ts);
        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "SOREN_NORTH", "SOREN_LAPTOP" }, records.Select(r => r.Machine));
    }

    [Fact]
    public void Appending_nothing_writes_nothing()
    {
        Assert.Equal(0, _log.Append(Array.Empty<PromptRecord>()));
    }
}
