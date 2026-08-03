using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// Remove-the-network-port mission, phase 3: THE TEST THAT WOULD HAVE CAUGHT THE WRONG DESIGN.
///
/// The obvious way to replace the deleted preamble routes is to write the file once, at session launch.
/// Every other test in this phase passes under that design. This one does not, and that is why it exists.
///
/// The preamble renders from stores that are LIVE: the user's own injected text, which they edit in
/// Settings while sessions are running, plus the workflow index and the skill index, both re-downloaded
/// from the Gateway on the Director's interval. The SessionStart hook fires again on every resume, clear
/// and compact - possibly hours after launch. Under a launch snapshot, a user who edits their text and
/// then clears the context is served the OLD text, and a skill published this morning is invisible to
/// every session started yesterday. Nothing throws, nothing turns red, and the only symptom is an agent
/// working from instructions nobody meant it to have.
///
/// So the assertions below are all of the form "change an input, then read what the NEXT hook fire would
/// deliver to an ALREADY-RUNNING session".
/// </summary>
public sealed class SessionPreambleMaintainerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _cachePath;
    private readonly SessionManager _sessions;

    public SessionPreambleMaintainerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccd-preamble-maint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cachePath = Path.Combine(_dir, "injected-text-cache.json");
        _sessions = new SessionManager(new AgentOptions());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Session Adopt(string? name = "maintainer-test")
    {
        var session = new Session(
            Guid.NewGuid(),
            repoPath: Path.Combine(_dir, "repo"),
            workingDirectory: Path.Combine(_dir, "repo"),
            claudeArgs: null,
            backend: new StubSessionBackend(),
            claudeSessionId: null,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: name,
            customColor: null);
        _sessions.AdoptSession(session);
        return session;
    }

    /// <summary>A store over this test's own cache file, so nothing here reads the machine's real cache.</summary>
    private InjectedTextStore Store() => new(_cachePath);

    private void SetUsersText(string text)
        => Store().WriteCache(new InjectedTextCacheEntry(UseYours: true, Yours: text, CachedAtUtc: DateTime.UtcNow));

    private SessionPreambleMaintainer Maintainer()
        => new(_sessions, () => null, _dir, machine: "TEST-MACHINE", store: Store());

    /// <summary>
    /// What the session's SessionStart hook would print if it fired RIGHT NOW - read exactly as the hook
    /// reads it, from the file, with no knowledge of how it got there.
    /// </summary>
    private string WhatTheNextHookFireDelivers(Session session)
    {
        var path = SessionHookFiles.PreamblePathFor(session.Id, _dir);
        if (!File.Exists(path))
            return "";
        var body = File.ReadAllText(path);
        if (body.Length == 0)
            return "";
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString() ?? "";
    }

    // ---------- The point of the whole design ----------

    /// <summary>
    /// A session that is ALREADY RUNNING. The user edits their injected text. The next hook fire must
    /// deliver the new text. Under a launch-time snapshot this test fails: the file still holds the text
    /// from before the edit.
    /// </summary>
    [Fact]
    public void Editing_the_injected_text_changes_what_the_next_hook_fire_delivers_to_a_running_session()
    {
        SetUsersText("FIRST VERSION OF MY RULES");
        var maintainer = Maintainer();
        maintainer.Start();
        var session = Adopt();

        Assert.Contains("FIRST VERSION OF MY RULES", WhatTheNextHookFireDelivers(session));

        // The user edits their text in Settings while the session runs; the Director's refresh downloads
        // it and rewrites every live session's file.
        SetUsersText("SECOND VERSION - THE ONE THEY MEANT");
        maintainer.RewriteAll();

        var delivered = WhatTheNextHookFireDelivers(session);
        Assert.Contains("SECOND VERSION - THE ONE THEY MEANT", delivered);
        Assert.DoesNotContain("FIRST VERSION OF MY RULES", delivered);
    }

    /// <summary>
    /// The same property for the OTHER direction of the same setting: a user who switches from their own
    /// text back to DevThrottle's, mid-session, gets ours on the next fire.
    /// </summary>
    [Fact]
    public void Switching_back_to_the_DevThrottle_text_mid_session_reaches_the_next_hook_fire()
    {
        SetUsersText("ONLY MY RULES");
        var maintainer = Maintainer();
        maintainer.Start();
        var session = Adopt();
        Assert.DoesNotContain("cc-devthrottle", WhatTheNextHookFireDelivers(session));

        Store().WriteCache(new InjectedTextCacheEntry(UseYours: false, Yours: "ONLY MY RULES", CachedAtUtc: DateTime.UtcNow));
        maintainer.RewriteAll();

        var delivered = WhatTheNextHookFireDelivers(session);
        Assert.Contains("cc-devthrottle", delivered);
        Assert.DoesNotContain("ONLY MY RULES", delivered);
    }

    /// <summary>
    /// A user whose text becomes unreadable mid-session must be injected NOTHING on the next fire - not
    /// the stale copy that is already in the file, and not ours. Rewriting to empty is a real change, so
    /// the maintainer has to make it rather than leave the last good render in place.
    /// </summary>
    [Fact]
    public void Text_that_becomes_unreadable_mid_session_empties_the_file_rather_than_serving_the_stale_copy()
    {
        SetUsersText("MY RULES");
        var maintainer = Maintainer();
        maintainer.Start();
        var session = Adopt();
        Assert.Contains("MY RULES", WhatTheNextHookFireDelivers(session));

        Store().WriteCache(new InjectedTextCacheEntry(UseYours: true, Yours: null, CachedAtUtc: DateTime.UtcNow));
        maintainer.RewriteAll();

        Assert.Equal("", File.ReadAllText(SessionHookFiles.PreamblePathFor(session.Id, _dir)));
    }

    // ---------- The per-session inputs, each on its own trigger ----------

    [Fact]
    public void Taking_a_workflow_seat_mid_session_reaches_the_next_hook_fire()
    {
        SetUsersText("MY RULES");
        var maintainer = Maintainer();
        maintainer.Start();
        var session = Adopt();
        Assert.DoesNotContain("[Workflow seat]", WhatTheNextHookFireDelivers(session));

        session.SetExplicitRole("Manager");
        session.SeatOnWorkflow(Guid.NewGuid(), "mission", 4);

        var delivered = WhatTheNextHookFireDelivers(session);
        Assert.Contains("[Workflow seat]", delivered);
        Assert.Contains("seated as Manager on the 'mission' workflow", delivered);
        Assert.Contains("--version 4", delivered);
    }

    [Fact]
    public void Renaming_a_session_reaches_the_next_hook_fire()
    {
        var maintainer = Maintainer();
        maintainer.Start();
        var session = Adopt("before the rename");
        Assert.Contains("before the rename", WhatTheNextHookFireDelivers(session));

        _sessions.RenameSession(session.Id, "after the rename");

        var delivered = WhatTheNextHookFireDelivers(session);
        Assert.Contains("after the rename", delivered);
        Assert.DoesNotContain("before the rename", delivered);
    }

    // ---------- Lifecycle ----------

    [Fact]
    public void A_session_that_joins_the_roster_gets_a_file_without_being_asked()
    {
        var maintainer = Maintainer();
        maintainer.Start();

        var session = Adopt();

        Assert.True(File.Exists(SessionHookFiles.PreamblePathFor(session.Id, _dir)));
    }

    /// <summary>
    /// Sessions restored from persistence at Director startup were never launched by this process, so
    /// nothing on the launch path would ever write theirs. Start() has to sweep what is already there.
    /// </summary>
    [Fact]
    public void Sessions_already_on_the_roster_are_written_when_the_maintainer_starts()
    {
        var session = Adopt();
        Assert.False(File.Exists(SessionHookFiles.PreamblePathFor(session.Id, _dir)));

        Maintainer().Start();

        Assert.True(File.Exists(SessionHookFiles.PreamblePathFor(session.Id, _dir)));
    }

    [Fact]
    public void A_removed_session_leaves_no_file_behind()
    {
        var maintainer = Maintainer();
        maintainer.Start();
        var session = Adopt();
        var path = SessionHookFiles.PreamblePathFor(session.Id, _dir);
        Assert.True(File.Exists(path));

        _sessions.RemoveSession(session.Id);

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// RewriteAll runs on the Director's refresh interval for every live session. A rewrite that changes
    /// nothing must not touch the file - otherwise a quiet Director rewrites every session's preamble
    /// every minute for no reason.
    /// </summary>
    [Fact]
    public void An_unchanged_rewrite_does_not_touch_the_file()
    {
        SetUsersText("MY RULES");
        var maintainer = Maintainer();
        maintainer.Start();
        var session = Adopt();
        var path = SessionHookFiles.PreamblePathFor(session.Id, _dir);
        var writtenAt = File.GetLastWriteTimeUtc(path);

        Assert.False(maintainer.Rewrite(session), "an unchanged rewrite reported a change");
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(path));

        SetUsersText("DIFFERENT RULES");
        Assert.True(maintainer.Rewrite(session), "a changed input did not report a change");
    }

    [Fact]
    public void Disposing_stops_following_the_roster()
    {
        var maintainer = Maintainer();
        maintainer.Start();
        maintainer.Dispose();

        var session = Adopt();

        Assert.False(File.Exists(SessionHookFiles.PreamblePathFor(session.Id, _dir)));
    }
}
