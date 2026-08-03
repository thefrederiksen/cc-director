using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// Remove-the-network-port mission, phase 3: the drop box that replaced
/// <c>POST /sessions/{sid}/claude-hook</c>.
///
/// The stakes are the same as the route's. Claude mints a new session id and a new transcript file on
/// <c>/clear</c> and on auto-compaction; without this report the Director's pointer goes stale, and
/// session history - plus the Gateway voice mode built on it - quietly goes empty. Nothing throws.
///
/// These drive <see cref="SessionPointerWatcher.Apply"/> and <see cref="SessionPointerWatcher.Sweep"/>
/// directly rather than starting the watcher, so no assertion here waits on a file-system notification.
/// The live watcher - a real drop, seen by a real <see cref="FileSystemWatcher"/> - is proven separately
/// in the suite that is allowed to spend wall-clock on it.
/// </summary>
public sealed class SessionPointerDropTests : IDisposable
{
    private readonly string _dir;
    private readonly SessionManager _sessions;

    public SessionPointerDropTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccd-pointer-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sessions = new SessionManager(new AgentOptions());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Session Adopt()
    {
        var session = new Session(
            Guid.NewGuid(),
            repoPath: Path.Combine(_dir, "repo"),
            workingDirectory: Path.Combine(_dir, "repo"),
            claudeArgs: null,
            backend: new StubSessionBackend(),
            claudeSessionId: "the-id-from-launch",
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "pointer-drop-test",
            customColor: null);
        _sessions.AdoptSession(session);
        return session;
    }

    private SessionPointerWatcher Watcher() => new(_sessions, _dir);

    /// <summary>Write a drop exactly as the hook script does: the raw Claude event, at the path the
    /// Director handed that session.</summary>
    private void Drop(Guid sessionId, string claudeId, string transcript, string source = "clear")
        => File.WriteAllText(
            SessionHookFiles.PointerPathFor(sessionId, _dir),
            $$"""{"session_id":"{{claudeId}}","transcript_path":"{{transcript}}","hook_event_name":"SessionStart","source":"{{source}}","cwd":"/tmp"}""");

    [Fact]
    public void A_drop_moves_the_sessions_pointer_to_the_rotated_transcript()
    {
        var session = Adopt();
        var rotatedId = Guid.NewGuid().ToString();
        Drop(session.Id, rotatedId, "/tmp/rotated.jsonl");

        Assert.True(Watcher().Apply(SessionHookFiles.PointerPathFor(session.Id, _dir)));

        Assert.Equal(rotatedId, session.ClaudeSessionId);
        Assert.Equal("/tmp/rotated.jsonl", session.ClaudeTranscriptPath);
    }

    /// <summary>
    /// The routing map, not just the session record. This is what a later lookup by Claude session id
    /// goes through, and the deleted route updated it in the same breath - a drop that moved one and not
    /// the other would leave the Director half-relinked.
    /// </summary>
    [Fact]
    public void A_drop_relinks_the_managers_claude_id_routing_map()
    {
        var session = Adopt();
        var rotatedId = Guid.NewGuid().ToString();
        Drop(session.Id, rotatedId, "/tmp/rotated.jsonl");

        Watcher().Apply(SessionHookFiles.PointerPathFor(session.Id, _dir));

        Assert.Equal(session.Id, _sessions.GetSessionByClaudeId(rotatedId)?.Id);
    }

    /// <summary>
    /// A drop is removed once it has been applied. That is what keeps the box empty in the steady state,
    /// so the two-second sweep that guarantees delivery costs almost nothing, and it means the hook's
    /// next write usually creates a file rather than replacing one.
    /// </summary>
    [Fact]
    public void An_applied_drop_is_removed()
    {
        var session = Adopt();
        Drop(session.Id, "rotated", "/tmp/rotated.jsonl");
        var path = SessionHookFiles.PointerPathFor(session.Id, _dir);

        Assert.True(Watcher().Apply(path));

        Assert.False(File.Exists(path), "an applied drop was left in the box");
    }

    /// <summary>
    /// Applying the same drop twice must be a no-op, because nothing in this channel guarantees a file is
    /// seen exactly once: a watcher event and the sweep can both deliver the same one. Re-dropped rather
    /// than re-read, because an applied drop is deleted - the second delivery is a second arrival of the
    /// same content, which is the case that actually happens.
    /// </summary>
    [Fact]
    public void Delivering_the_same_drop_twice_changes_nothing()
    {
        var session = Adopt();
        var rotatedId = Guid.NewGuid().ToString();
        var watcher = Watcher();
        var path = SessionHookFiles.PointerPathFor(session.Id, _dir);

        Drop(session.Id, rotatedId, "/tmp/rotated.jsonl");
        Assert.True(watcher.Apply(path));
        Drop(session.Id, rotatedId, "/tmp/rotated.jsonl");
        Assert.True(watcher.Apply(path));

        Assert.Equal(rotatedId, session.ClaudeSessionId);
        Assert.Equal("/tmp/rotated.jsonl", session.ClaudeTranscriptPath);
    }

    /// <summary>
    /// A drop that is gone by the time it is read - the other delivery path applied and deleted it first.
    /// The two paths make that race routine, so it must be quiet rather than an error, and it must not
    /// disturb the pointer either way.
    /// </summary>
    [Fact]
    public void A_drop_that_has_already_been_applied_and_removed_is_not_an_error()
    {
        var session = Adopt();
        var path = SessionHookFiles.PointerPathFor(session.Id, _dir);

        Assert.False(Watcher().Apply(path));
        Assert.Equal("the-id-from-launch", session.ClaudeSessionId);
    }

    /// <summary>
    /// The whole point of sweeping: a drop that NO notification ever arrived for is still delivered. This
    /// is the unit-level statement of the defect that made the sweep the delivery path - the watcher was
    /// observed to lose a notification for a drop that was present, complete and valid.
    /// </summary>
    [Fact]
    public void A_drop_no_notification_ever_arrived_for_is_still_delivered_by_a_sweep()
    {
        var session = Adopt();
        var rotatedId = Guid.NewGuid().ToString();

        // Nothing is watching this directory - no watcher was ever started, so no event exists.
        Drop(session.Id, rotatedId, "/tmp/unnotified.jsonl");

        Assert.Equal(1, Watcher().Sweep());
        Assert.Equal(rotatedId, session.ClaudeSessionId);
        Assert.Equal("/tmp/unnotified.jsonl", session.ClaudeTranscriptPath);
    }

    /// <summary>
    /// THE SESSION COMES FROM THE FILE NAME, NEVER FROM THE BODY. The Director hands each session the
    /// exact path to write, so this is what stops a drop being applied to a session other than the one it
    /// was written for - the shape of the drop box doing what the route's session-bound credential did.
    /// </summary>
    [Fact]
    public void A_drop_is_applied_to_the_session_its_FILE_names_not_one_named_inside_it()
    {
        var mine = Adopt();
        var other = Adopt();

        // A body that names the OTHER session in every field a body could carry.
        File.WriteAllText(SessionHookFiles.PointerPathFor(mine.Id, _dir),
            $$"""{"session_id":"hijack","transcript_path":"/tmp/hijack.jsonl","sessionId":"{{other.Id}}","cc_session_id":"{{other.Id}}"}""");

        Watcher().Apply(SessionHookFiles.PointerPathFor(mine.Id, _dir));

        Assert.Equal("hijack", mine.ClaudeSessionId);
        Assert.Equal("the-id-from-launch", other.ClaudeSessionId);
    }

    [Fact]
    public void A_drop_for_a_session_that_is_not_on_the_roster_is_ignored()
    {
        var stranger = Guid.NewGuid();
        Drop(stranger, "whatever", "/tmp/whatever.jsonl");

        Assert.False(Watcher().Apply(SessionHookFiles.PointerPathFor(stranger, _dir)));
    }

    [Fact]
    public void A_file_whose_name_is_not_a_session_id_is_ignored()
    {
        var path = Path.Combine(_dir, "not-a-session-id.json");
        File.WriteAllText(path, """{"session_id":"x"}""");

        Assert.False(Watcher().Apply(path));
    }

    [Fact]
    public void A_drop_that_is_not_valid_json_leaves_the_pointer_alone()
    {
        var session = Adopt();
        File.WriteAllText(SessionHookFiles.PointerPathFor(session.Id, _dir), "this is not json {");

        Assert.False(Watcher().Apply(SessionHookFiles.PointerPathFor(session.Id, _dir)));
        Assert.Equal("the-id-from-launch", session.ClaudeSessionId);
    }

    /// <summary>
    /// The half-written file an atomic write leaves behind for an instant. The watcher filters it out by
    /// extension and this asserts the second line of that defence, because a Windows filename filter has
    /// historically matched more than it appears to.
    /// </summary>
    [Fact]
    public void A_temporary_file_from_an_in_progress_write_is_never_applied()
    {
        var session = Adopt();
        var tmp = Path.ChangeExtension(SessionHookFiles.PointerPathFor(session.Id, _dir), ".tmp");
        File.WriteAllText(tmp, """{"session_id":"half-written","transcript_path":"/tmp/half.jsonl"}""");

        Assert.False(Watcher().Apply(tmp));
        Assert.Equal("the-id-from-launch", session.ClaudeSessionId);
    }

    [Fact]
    public void A_sweep_applies_every_drop_in_the_box_and_ignores_the_rest()
    {
        var a = Adopt();
        var b = Adopt();
        Drop(a.Id, "rotated-a", "/tmp/a.jsonl");
        Drop(b.Id, "rotated-b", "/tmp/b.jsonl");
        Drop(Guid.NewGuid(), "rotated-stranger", "/tmp/stranger.jsonl");
        File.WriteAllText(Path.Combine(_dir, "rubbish.json"), "not json {");

        Assert.Equal(2, Watcher().Sweep());

        Assert.Equal("rotated-a", a.ClaudeSessionId);
        Assert.Equal("rotated-b", b.ClaudeSessionId);

        // The two that were applied are gone; the two that were not stay put. A sweep that deleted what
        // it could not apply would throw away a drop for a session that is merely still starting up.
        Assert.False(File.Exists(SessionHookFiles.PointerPathFor(a.Id, _dir)));
        Assert.False(File.Exists(SessionHookFiles.PointerPathFor(b.Id, _dir)));
        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
    }

    /// <summary>
    /// The camelCase shape the Windows script used to build, still accepted. Both scripts now write
    /// Claude's raw event verbatim, but the mapping is what the parser's own tests pin and dropping it
    /// would break a hook file left over from an older install between an update and the next launch.
    /// </summary>
    [Fact]
    public void The_older_mapped_body_shape_is_still_understood()
    {
        var session = Adopt();
        File.WriteAllText(SessionHookFiles.PointerPathFor(session.Id, _dir),
            """{"claudeSessionId":"mapped-id","transcriptPath":"/tmp/mapped.jsonl","hookEvent":"SessionStart","source":"compact"}""");

        Assert.True(Watcher().Apply(SessionHookFiles.PointerPathFor(session.Id, _dir)));
        Assert.Equal("mapped-id", session.ClaudeSessionId);
        Assert.Equal("/tmp/mapped.jsonl", session.ClaudeTranscriptPath);
    }

    /// <summary>
    /// Forgetting a session removes its drop, so a reaped session cannot have its file re-applied to an
    /// id that comes round again.
    /// </summary>
    [Fact]
    public void Forgetting_a_session_removes_its_drop()
    {
        var session = Adopt();
        Drop(session.Id, "rotated", "/tmp/rotated.jsonl");
        var path = SessionHookFiles.PointerPathFor(session.Id, _dir);

        Watcher().Forget(session.Id);

        Assert.False(File.Exists(path));
    }
}
