using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tests.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The desktop rail paints a session ORANGE while it has held/queued desktop dictations
/// (SessionViewModel reads <see cref="Session.PendingDictationCount"/> &gt; 0, alongside the
/// phone-arrival <see cref="Session.IsReceivingDictation"/>). These pin the model contract that
/// drives that repaint: the count is change-notifying and coalesces no-op writes, so the rail is
/// nudged exactly when the held-dictation queue actually changes and not on every roster tick.
/// </summary>
public sealed class SessionPendingDictationTests
{
    private static Session CreateSession(SessionManager manager)
        => manager.CreateEmbeddedSession(System.IO.Path.GetTempPath(), null, new BufferOnlyBackend());

    [Fact]
    public void PendingDictationCount_raises_change_event_with_new_value()
    {
        using var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var session = CreateSession(manager);

        var observed = new System.Collections.Generic.List<int>();
        session.OnPendingDictationCountChanged += observed.Add;

        session.PendingDictationCount = 7;   // clips queued
        session.PendingDictationCount = 0;   // queue drained

        Assert.Equal(new[] { 7, 0 }, observed);
        Assert.Equal(0, session.PendingDictationCount);
    }

    [Fact]
    public void PendingDictationCount_setting_same_value_does_not_re_notify()
    {
        using var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var session = CreateSession(manager);

        var fires = 0;
        session.OnPendingDictationCountChanged += _ => fires++;

        session.PendingDictationCount = 3;
        session.PendingDictationCount = 3;   // no change - the roster tick re-syncs the same count

        Assert.Equal(1, fires);
    }
}
