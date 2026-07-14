using CcDirector.Core.Configuration;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Tests.Wingman; // BufferOnlyBackend (internal test stub)
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Tests for the durable prompt + reply record (issue #1551): the origin sidecar
/// (<see cref="InputOriginLog"/>), the content log (<see cref="ConversationLog"/>), and the join
/// between them. All methods share an isolated CC_DIRECTOR_ROOT, set in the constructor; xUnit runs
/// the methods of one class sequentially so the shared env var is safe within the class.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class PromptLogTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public PromptLogTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-promptlog-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ===== the content log =====

    private static ConversationRecord Sample(DateTime ts, string text, string role = "user") => new()
    {
        TsUtc = ts,
        SessionId = "session-1",
        SessionName = "devthrottle / abcd",
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
    public void ConversationLog_round_trips_a_message()
    {
        var ts = new DateTime(2026, 7, 14, 9, 30, 0, DateTimeKind.Utc);
        ConversationLog.Write(Sample(ts, "fix the login bug"));

        var only = Assert.Single(ConversationLog.Read(ts, ts));
        Assert.Equal("fix the login bug", only.Text);
        Assert.Equal("user", only.Role);
        Assert.Equal("typed", only.Modality);
        Assert.Equal("desktop", only.Surface);
        Assert.Equal(4, only.WordCount);
    }

    [Fact]
    public void ConversationLog_appends_rather_than_overwriting()
    {
        var ts = new DateTime(2026, 7, 14, 9, 30, 0, DateTimeKind.Utc);
        ConversationLog.Write(Sample(ts, "first"));
        ConversationLog.Write(Sample(ts.AddMinutes(1), "second", "assistant"));
        ConversationLog.Write(Sample(ts.AddMinutes(2), "third"));

        var read = ConversationLog.Read(ts, ts);

        Assert.Equal(new[] { "first", "second", "third" }, read.Select(r => r.Text));
        Assert.Equal(new[] { "user", "assistant", "user" }, read.Select(r => r.Role));
    }

    [Fact]
    public void ConversationLog_spans_days_and_returns_oldest_first()
    {
        var day1 = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        var day3 = new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Utc);
        ConversationLog.Write(Sample(day1, "monday"));
        ConversationLog.Write(Sample(day3, "wednesday"));

        Assert.Equal(new[] { "monday", "wednesday" }, ConversationLog.Read(day1, day3).Select(r => r.Text));
    }

    [Fact]
    public void ConversationLog_read_of_an_absent_day_is_empty_rather_than_throwing()
    {
        var ts = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        Assert.Empty(ConversationLog.Read(ts, ts));
    }

    [Fact]
    public void ConversationLog_skips_a_corrupt_line_and_still_returns_the_good_ones()
    {
        var ts = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        ConversationLog.Write(Sample(ts, "good one"));
        File.AppendAllText(ConversationLog.FileFor(ts), "{ this is not json" + Environment.NewLine);
        ConversationLog.Write(Sample(ts.AddMinutes(1), "good two"));

        Assert.Equal(new[] { "good one", "good two" }, ConversationLog.Read(ts, ts).Select(r => r.Text));
    }

    [Fact]
    public void ConversationLog_preserves_a_multi_line_message_as_one_record()
    {
        var ts = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        var text = "line one\nline two\nline three";
        ConversationLog.Write(Sample(ts, text));

        Assert.Equal(text, Assert.Single(ConversationLog.Read(ts, ts)).Text);
    }

    // ===== the origin sidecar, written at the Session choke points =====

    private static IReadOnlyList<InputOriginRecord> OriginsToday()
        => InputOriginLog.Read(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1));

    private static Session NewSession()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        return manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferOnlyBackend());
    }

    [Fact]
    public async Task SendTextAsync_records_where_the_prompt_came_from()
    {
        var session = NewSession();

        await session.SendTextAsync("make the button blue", SendSource.UserInput, InputOrigin.DesktopTyped);

        var only = Assert.Single(OriginsToday());
        Assert.Equal(session.Id.ToString(), only.SessionId);
        Assert.Equal("typed", only.Modality);
        Assert.Equal("desktop", only.Surface);
        Assert.Equal(20, only.CharCount);
    }

    [Fact]
    public async Task SendTextAsync_records_the_origin_it_was_given()
    {
        var session = NewSession();

        await session.SendTextAsync("spoken from the phone", SendSource.UserInput, InputOrigin.Voice(InputSurface.Phone));

        var only = Assert.Single(OriginsToday());
        Assert.Equal("voice", only.Modality);
        Assert.Equal("phone", only.Surface);
    }

    [Fact]
    public async Task SendTextAsync_without_an_origin_is_framework_internal_and_is_not_recorded()
    {
        var session = NewSession();

        await session.SendTextAsync("handover framing text", SendSource.UserInput, origin: null);

        Assert.Empty(OriginsToday());
    }

    [Fact]
    public async Task SendTextAsync_with_blank_text_records_nothing()
    {
        var session = NewSession();

        await session.SendTextAsync("", SendSource.UserInput, InputOrigin.DesktopTyped);

        Assert.Empty(OriginsToday());
    }

    [Fact]
    public void Terminal_typing_records_one_origin_event_on_submit_not_one_per_keystroke()
    {
        var session = NewSession();

        // Typing "hi" at the terminal grid: one keystroke at a time, then Enter.
        session.SendInput(new[] { (byte)'h' }, InputOrigin.DesktopTyped);
        session.SendInput(new[] { (byte)'i' }, InputOrigin.DesktopTyped);
        session.SendInput(new byte[] { 0x0D }, InputOrigin.DesktopTyped);

        var only = Assert.Single(OriginsToday());
        Assert.Equal("typed", only.Modality);
        Assert.Equal("desktop", only.Surface);
        // The whole line's size, not just the final keystroke's.
        Assert.Equal(2, only.CharCount);
    }

    [Fact]
    public void Terminal_typing_without_a_submit_records_nothing_yet()
    {
        var session = NewSession();

        session.SendInput(new[] { (byte)'h' }, InputOrigin.DesktopTyped);
        session.SendInput(new[] { (byte)'i' }, InputOrigin.DesktopTyped);

        // Still composing - no submission has happened, so there is no prompt to attribute.
        Assert.Empty(OriginsToday());
    }

    [Fact]
    public void Terminal_typing_starts_a_fresh_count_after_each_submit()
    {
        var session = NewSession();

        session.SendInput(new[] { (byte)'a' }, InputOrigin.DesktopTyped);
        session.SendInput(new byte[] { 0x0D }, InputOrigin.DesktopTyped);
        session.SendInput(new[] { (byte)'b' }, InputOrigin.DesktopTyped);
        session.SendInput(new[] { (byte)'c' }, InputOrigin.DesktopTyped);
        session.SendInput(new byte[] { 0x0D }, InputOrigin.DesktopTyped);

        var events = OriginsToday();
        Assert.Equal(2, events.Count);
        Assert.Equal(1, events[0].CharCount);
        Assert.Equal(2, events[1].CharCount); // not 3 - the first line's chars are not carried over
    }
}
