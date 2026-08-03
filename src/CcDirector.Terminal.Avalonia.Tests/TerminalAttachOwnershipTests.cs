using System;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// One live terminal view per session.
///
/// Attaching a session is not a read - <see cref="TerminalControl.Attach"/> ends by sending
/// Session.Resize, which resizes the real ConPTY the agent writes into. Two controls attached to
/// one session each poll it and each resize it to their own grid, so the agent's output reflows
/// on every repaint.
///
/// This shipped: the FIFO takeover window attached sessions to its own terminal while the main
/// window was still attached to the same session, and the main window "repaired" the geometry
/// after FIFO closed rather than avoiding the overlap. These tests pin the invariant that made
/// that a bug, so a future caller that forgets to detach is caught here instead of by a user
/// watching their transcript reflow.
/// </summary>
public sealed class TerminalAttachOwnershipTests
{
    private static Session NewSession() =>
        new(Guid.NewGuid(), repoPath: @"C:\repo", workingDirectory: @"C:\repo", claudeArgs: null,
            backend: new NullBackend(), claudeSessionId: null,
            activityState: ActivityState.WaitingForInput,
            createdAt: DateTimeOffset.UtcNow, customName: null, customColor: null);

    private static TerminalControl NewAttachedTerminal()
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 400, Content = terminal };
        window.Show();
        return terminal;
    }

    [AvaloniaFact]
    public void Attach_TakesOwnership()
    {
        using var session = NewSession();
        var terminal = NewAttachedTerminal();

        terminal.Attach(session);

        Assert.Same(terminal, TerminalControl.OwnerOf(session.Id));
    }

    [AvaloniaFact]
    public void SecondAttach_EvictsTheFirstControl()
    {
        // The FIFO case: the main window is showing a session and FIFO attaches the same one.
        using var session = NewSession();
        var mainWindowTerminal = NewAttachedTerminal();
        var fifoTerminal = NewAttachedTerminal();

        mainWindowTerminal.Attach(session);
        fifoTerminal.Attach(session);

        // Exactly one owner, and it is the newcomer.
        Assert.Same(fifoTerminal, TerminalControl.OwnerOf(session.Id));

        // And the displaced control is genuinely detached - not merely un-owned - so it has
        // stopped polling and will not resize the session out from under the new owner.
        Assert.False(mainWindowTerminal.IsAttachedForTests);
        Assert.True(fifoTerminal.IsAttachedForTests);
    }

    [AvaloniaFact]
    public void EvictedControlDetaching_DoesNotClearTheNewOwner()
    {
        // The ordering that makes a naive registry wrong: the evicted control detaches LATER
        // (its window closes), and must not take the new owner's claim with it.
        using var session = NewSession();
        var first = NewAttachedTerminal();
        var second = NewAttachedTerminal();

        first.Attach(session);
        second.Attach(session);
        first.Detach();

        Assert.Same(second, TerminalControl.OwnerOf(session.Id));
        Assert.True(second.IsAttachedForTests);
    }

    [AvaloniaFact]
    public void Detach_ReleasesOwnership()
    {
        using var session = NewSession();
        var terminal = NewAttachedTerminal();

        terminal.Attach(session);
        terminal.Detach();

        Assert.Null(TerminalControl.OwnerOf(session.Id));
    }

    [AvaloniaFact]
    public void ReAttachingSameControl_KeepsOwnershipAndStaysAttached()
    {
        // MainWindow re-attaches its own session after FIFO closes. Attach() calls Detach()
        // internally, so the control must not release and then fail to reclaim itself.
        using var session = NewSession();
        var terminal = NewAttachedTerminal();

        terminal.Attach(session);
        terminal.Attach(session);

        Assert.Same(terminal, TerminalControl.OwnerOf(session.Id));
        Assert.True(terminal.IsAttachedForTests);
    }

    [AvaloniaFact]
    public void SwitchingSessions_ReleasesTheOldOne()
    {
        using var first = NewSession();
        using var second = NewSession();
        var terminal = NewAttachedTerminal();

        terminal.Attach(first);
        terminal.Attach(second);

        Assert.Null(TerminalControl.OwnerOf(first.Id));
        Assert.Same(terminal, TerminalControl.OwnerOf(second.Id));
    }

    [AvaloniaFact]
    public void DistinctSessions_DoNotContend()
    {
        // The ordinary grid case: separate sessions in separate panes each keep their own view.
        using var a = NewSession();
        using var b = NewSession();
        var paneOne = NewAttachedTerminal();
        var paneTwo = NewAttachedTerminal();

        paneOne.Attach(a);
        paneTwo.Attach(b);

        Assert.Same(paneOne, TerminalControl.OwnerOf(a.Id));
        Assert.Same(paneTwo, TerminalControl.OwnerOf(b.Id));
        Assert.True(paneOne.IsAttachedForTests);
        Assert.True(paneTwo.IsAttachedForTests);
    }

    /// <summary>A backend that starts nothing - the session only needs to exist and have an id.</summary>
    private sealed class NullBackend : ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Test";
        public bool IsRunning => false;
        public bool HasExited => true;
        public CircularTerminalBuffer? Buffer { get; } = new(1024);
        public event Action<string>? StatusChanged { add { } remove { } }
        public event Action<int>? ProcessExited { add { } remove { } }
        public void Start(string executable, string args, string workingDir, short cols, short rows,
            System.Collections.Generic.Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public System.Threading.Tasks.Task SendTextAsync(string text) => System.Threading.Tasks.Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public System.Threading.Tasks.Task GracefulShutdownAsync(int timeoutMs = 5000)
            => System.Threading.Tasks.Task.CompletedTask;
        public void Dispose() { }
    }
}
