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

    /// <summary>The exact path the Director hands this session - id dot its own drop token.</summary>
    private string PathFor(Session session)
        => SessionHookFiles.PointerPathFor(session.Id, session.PointerDropToken, _dir);

    // GUIDs, because real Claude session ids are. Since #2456 a drop whose id is not a GUID is
    // refused whole, so a readable label here would fail against a guard that is working.
    private const string RotatedId = "cccccccc-7777-4777-8777-cccccccccccc";
    private const string RotatedAId = "dddddddd-8888-4888-8888-dddddddddddd";
    private const string RotatedBId = "eeeeeeee-9999-4999-8999-eeeeeeeeeeee";
    private const string MappedId = "aaaaaaaa-5555-4555-8555-aaaaaaaaaaaa";
    private const string HijackId = "bbbbbbbb-6666-4666-8666-bbbbbbbbbbbb";

    private static string Body(string claudeId, string transcript, string source = "clear")
        => $$"""{"session_id":"{{claudeId}}","transcript_path":"{{transcript}}","hook_event_name":"SessionStart","source":"{{source}}","cwd":"/tmp"}""";

    /// <summary>Write a drop exactly as the hook script does: the raw Claude event, at the path the
    /// Director handed that session.</summary>
    private void Drop(Session session, string claudeId, string transcript, string source = "clear")
        => File.WriteAllText(PathFor(session), Body(claudeId, transcript, source));

    [Fact]
    public void A_drop_moves_the_sessions_pointer_to_the_rotated_transcript()
    {
        var session = Adopt();
        var rotatedId = Guid.NewGuid().ToString();
        Drop(session, rotatedId, "/tmp/rotated.jsonl");

        Assert.True(Watcher().Apply(PathFor(session)));

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
        Drop(session, rotatedId, "/tmp/rotated.jsonl");

        Watcher().Apply(PathFor(session));

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
        Drop(session, RotatedId, "/tmp/rotated.jsonl");
        var path = PathFor(session);

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
        var path = PathFor(session);

        Drop(session, rotatedId, "/tmp/rotated.jsonl");
        Assert.True(watcher.Apply(path));
        Drop(session, rotatedId, "/tmp/rotated.jsonl");
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
        var path = PathFor(session);

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
        Drop(session, rotatedId, "/tmp/unnotified.jsonl");

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
        File.WriteAllText(PathFor(mine),
            $$"""{"session_id":"{{HijackId}}","transcript_path":"/tmp/hijack.jsonl","sessionId":"{{other.Id}}","cc_session_id":"{{other.Id}}"}""");

        Watcher().Apply(PathFor(mine));

        Assert.Equal(HijackId, mine.ClaudeSessionId);
        Assert.Equal("the-id-from-launch", other.ClaudeSessionId);
    }

    /// <summary>
    /// THE SIBLING-WRITE ATTACK - the one inspection 3 proved the old test never attempted. The drop
    /// box is one shared same-user directory, so any agent process can derive it from its own
    /// environment and write a file NAMED for a sibling live session. The name must not be enough:
    /// a drop is applied only when its name also carries the victim's unguessable token, which an
    /// attacker who can merely spell the victim's id does not have.
    /// </summary>
    [Fact]
    public void A_sibling_write_naming_a_victims_session_id_cannot_retarget_its_pointer()
    {
        var victim = Adopt();
        var watcher = Watcher();

        // The attack exactly as the inspector described it: a valid hook body, at the path the OLD
        // scheme authorized - the victim's bare session id.
        var bareIdPath = Path.Combine(_dir, victim.Id + ".json");
        File.WriteAllText(bareIdPath, Body("attacker-id", "/tmp/attacker.jsonl"));

        Assert.False(watcher.Apply(bareIdPath), "a drop named by session id alone was applied");

        // The same attack with a token the attacker minted for itself: right shape, wrong secret.
        var guessedPath = SessionHookFiles.PointerPathFor(victim.Id, SessionHookFiles.NewDropToken(), _dir);
        File.WriteAllText(guessedPath, Body("attacker-id", "/tmp/attacker.jsonl"));

        Assert.False(watcher.Apply(guessedPath), "a drop carrying a guessed token was applied");

        // A sweep - the delivery path that runs every two seconds in production - must refuse both too.
        Assert.Equal(0, watcher.Sweep());

        Assert.Equal("the-id-from-launch", victim.ClaudeSessionId);
        Assert.Null(victim.ClaudeTranscriptPath);
        Assert.Null(_sessions.GetSessionByClaudeId("attacker-id"));
    }

    [Fact]
    public void A_drop_for_a_session_that_is_not_on_the_roster_is_ignored()
    {
        var stranger = Guid.NewGuid();
        var path = SessionHookFiles.PointerPathFor(stranger, SessionHookFiles.NewDropToken(), _dir);
        File.WriteAllText(path, Body("whatever", "/tmp/whatever.jsonl"));

        Assert.False(Watcher().Apply(path));
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
        File.WriteAllText(PathFor(session), "this is not json {");

        Assert.False(Watcher().Apply(PathFor(session)));
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
        var tmp = Path.ChangeExtension(PathFor(session), ".tmp");
        File.WriteAllText(tmp, """{"session_id":"half-written","transcript_path":"/tmp/half.jsonl"}""");

        Assert.False(Watcher().Apply(tmp));
        Assert.Equal("the-id-from-launch", session.ClaudeSessionId);
    }

    [Fact]
    public void A_sweep_applies_every_drop_in_the_box_and_ignores_the_rest()
    {
        var a = Adopt();
        var b = Adopt();
        Drop(a, RotatedAId, "/tmp/a.jsonl");
        Drop(b, RotatedBId, "/tmp/b.jsonl");
        File.WriteAllText(
            SessionHookFiles.PointerPathFor(Guid.NewGuid(), SessionHookFiles.NewDropToken(), _dir),
            Body("rotated-stranger", "/tmp/stranger.jsonl"));
        File.WriteAllText(Path.Combine(_dir, "rubbish.json"), "not json {");

        Assert.Equal(2, Watcher().Sweep());

        Assert.Equal(RotatedAId, a.ClaudeSessionId);
        Assert.Equal(RotatedBId, b.ClaudeSessionId);

        // The two that were applied are gone; the two that were not stay put. A sweep that deleted what
        // it could not apply would throw away a drop for a session that is merely still starting up.
        Assert.False(File.Exists(PathFor(a)));
        Assert.False(File.Exists(PathFor(b)));
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
        File.WriteAllText(PathFor(session),
            $$"""{"claudeSessionId":"{{MappedId}}","transcriptPath":"/tmp/mapped.jsonl","hookEvent":"SessionStart","source":"compact"}""");

        Assert.True(Watcher().Apply(PathFor(session)));
        Assert.Equal(MappedId, session.ClaudeSessionId);
        Assert.Equal("/tmp/mapped.jsonl", session.ClaudeTranscriptPath);
    }

    // Issue #2456: the drop that destroyed three sessions. A body carrying the literal id "x" - no
    // event, no source, no transcript - was accepted over a verified GUID and persisted, after which
    // the session could never resolve its transcript and silently never narrated again.

    [Theory]
    [InlineData("x")]                        // the exact value from the incident
    [InlineData("hijack")]
    [InlineData("mapped-id")]
    [InlineData("57409e62-bd96-42f1-9fd8")]  // truncated GUID: the near miss
    public void A_drop_naming_a_non_guid_is_refused_whole(string malformed)
    {
        var session = Adopt();
        Drop(session, malformed, "/tmp/malformed.jsonl");

        Assert.False(Watcher().Apply(PathFor(session)));

        // BOTH writers must have been skipped. Guarding only UpdateClaudeSessionPointer is not enough:
        // RelinkClaudeSession assigns Session.ClaudeSessionId directly through its internal setter, and
        // in the original incident it is the line that actually persisted "x". A version of this fix
        // that guarded only the first passed every other test in this file while leaving the session
        // corrupted exactly as before.
        Assert.Equal("the-id-from-launch", session.ClaudeSessionId);
        Assert.Null(session.ClaudeTranscriptPath);
    }

    [Fact]
    public void A_refused_drop_is_deleted_so_the_sweep_does_not_retry_it_forever()
    {
        // A malformed body will never become valid. Left in the box, the two-second sweep would
        // re-read and re-log it for the life of the session.
        var session = Adopt();
        Drop(session, "x", "/tmp/malformed.jsonl");

        Assert.False(Watcher().Apply(PathFor(session)));

        Assert.False(File.Exists(PathFor(session)));
    }

    [Fact]
    public void A_refused_drop_does_not_break_the_routing_map_for_the_id_it_already_had()
    {
        // RelinkClaudeSession removes the OLD mapping before installing the new one, so a refusal that
        // reached it would unhook a working session from its own transcript even if the assignment
        // were somehow blocked. Refusing before that call is what keeps the old link intact.
        var session = Adopt();
        var good = Guid.NewGuid().ToString();
        Drop(session, good, "/tmp/good.jsonl");
        Assert.True(Watcher().Apply(PathFor(session)));

        Drop(session, "x", "/tmp/bad.jsonl");
        Assert.False(Watcher().Apply(PathFor(session)));

        Assert.Equal(good, session.ClaudeSessionId);
        Assert.Equal("/tmp/good.jsonl", session.ClaudeTranscriptPath);
    }

    /// <summary>
    /// Forgetting a session removes its drop, so a reaped session cannot have its file re-applied to an
    /// id that comes round again.
    /// </summary>
    [Fact]
    public void Forgetting_a_session_removes_its_drop()
    {
        var session = Adopt();
        Drop(session, RotatedId, "/tmp/rotated.jsonl");
        var path = PathFor(session);

        Watcher().Forget(session.Id);

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// A SHORT token is refused when the path is built, so a weak capability can never be minted.
    ///
    /// Raised by cross-family review of the fix itself: the method's own prose promised 32 characters
    /// while its check accepted any non-empty lowercase-hex run, so a one-character token would have
    /// produced a valid-looking drop path that an attacker could spell by guessing sixteen values.
    /// Nothing produces a short token today - <see cref="SessionHookFiles.NewDropToken"/> is the only
    /// mint and it always yields 32 - which is exactly why the gap needed a test rather than trust:
    /// the contract was enforced only by the comment beside it.
    /// </summary>
    [Theory]
    [InlineData("a")]                                   // one character
    [InlineData("0123456789abcde")]                     // 15 - just short
    [InlineData("0123456789abcdef0123456789abcdef0")]   // 33 - just long
    public void A_token_that_is_not_full_length_is_refused(string token)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SessionHookFiles.PointerPathFor(Guid.NewGuid(), token, Path.GetTempPath()));

        Assert.Contains(SessionHookFiles.DropTokenLength.ToString(), ex.Message);
    }

    /// <summary>The mint itself satisfies the rule it is checked against - the two cannot drift apart.</summary>
    [Fact]
    public void A_minted_token_is_accepted()
    {
        var token = SessionHookFiles.NewDropToken();

        Assert.Equal(SessionHookFiles.DropTokenLength, token.Length);
        var path = SessionHookFiles.PointerPathFor(Guid.NewGuid(), token, Path.GetTempPath());
        Assert.EndsWith("." + token + ".json", path);
    }
}
