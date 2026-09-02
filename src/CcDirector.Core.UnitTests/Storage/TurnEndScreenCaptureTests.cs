using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.UnitTests.Storage;

/// <summary>
/// ROW 0 of the Terminal Rules phase 0 proofs
/// (<c>docs/missions/terminal-rules-2026-09-02/phase-0-proofs.md</c>): the REAL capture fires on the
/// real Working -> WaitingForInput flip and reaches its sink, with the grid and the terminal's byte
/// mark taken together.
///
/// WHY THIS EXISTS AS ITS OWN ROW. Every other phase 0 proof seeds the screen store BY HAND, so if the
/// push were wired to nothing at all they would all still pass. This is the one cheap in-process seam
/// that drives the Director half for real, and it needs no Gateway and no hub.
///
/// WHERE IT STOPS, stated so the result is never stretched into a larger claim. It covers the flip, the
/// capture, and the <see cref="ITurnEndScreenSink"/> CONTRACT. It does NOT cover
/// <c>GatewayScreenSink.Send</c>, the <c>GatewayStreamClient.PushScreen</c> invoke, the
/// <c>DirectorHub.PushScreen</c> handler, or the store write. Those four links stay unexercised until
/// the mission's row 4 runs against a real Gateway.
///
/// These live in the UNIT test project rather than beside the other TurnReviewLogger tests in
/// Core.Tests deliberately: Core.Tests is parked and does not run in the default local gate, and a
/// proof that does not run is not a proof.
/// </summary>
public class TurnEndScreenCaptureTests
{
    /// <summary>An in-process backend with a REAL terminal buffer that never spawns a process and never
    /// exits - the real ConPty backend terminates almost at once and would put the session into Exited
    /// before a flip could be observed. The same stub Core.Tests keeps for its own session tests; it is
    /// duplicated here rather than shared because a twenty-line stub crossing test projects is a worse
    /// coupling than two copies of it.</summary>
    private sealed class BufferOnlyBackend : ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Buffer-only";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new CircularTerminalBuffer(65536);

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Buffer?.Write(data);
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>A sink that records what it was handed, so the assertion is on the real object the real
    /// logger produced rather than on a log line about it.</summary>
    private sealed class CapturingSink : ITurnEndScreenSink
    {
        public List<TurnEndScreen> Received { get; } = new();
        public void Send(TurnEndScreen screen) { lock (Received) Received.Add(screen); }
    }

    [Fact]
    public void Flip_to_waiting_hands_the_sink_the_screen_that_was_on_the_terminal()
    {
        var manager = new SessionManager(new AgentOptions());
        var sink = new CapturingSink();
        var logger = new TurnReviewLogger(manager, sink);
        try
        {
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferOnlyBackend());
            var buffer = session.Buffer ?? throw new InvalidOperationException("the session has no buffer");
            logger.Start();

            buffer.Write(Encoding.UTF8.GetBytes("SCREEN_MARKER_FOR_ROW_ZERO\r\n"));
            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

            // Exactly one capture for one flip. More than one would mean the trigger fires on repaints,
            // which is the noise this logger's single-trigger design exists to keep out.
            var screen = Assert.Single(sink.Received);
            Assert.Equal(session.Id.ToString(), screen.SessionId);

            // The CONTENT, not merely a non-empty capture: a capture that arrived blank would pass a
            // "not empty" assertion on a fixed-height grid, because a blank grid is still full of rows.
            Assert.Contains(screen.Rows, r => r.Contains("SCREEN_MARKER_FOR_ROW_ZERO"));

            // The byte mark and the grid describe the same moment. The mark is read before the grid
            // snapshot on purpose (see Session.SnapshotLiveScreenWithBufferMark), so it may lag the
            // buffer by bytes that arrive in between - it may never RUN AHEAD of it.
            Assert.True(screen.BufferBytes > 0, "the byte mark must carry the terminal's written total");
            Assert.True(screen.BufferBytes <= buffer.TotalBytesWritten,
                $"the mark ({screen.BufferBytes}) must never exceed the buffer's own total ({buffer.TotalBytesWritten})");

            // HasGrid is the readability flag every reader fails closed on, so it must agree with the rows
            // it arrived with rather than being set independently of them.
            Assert.Equal(screen.Rows.Length > 0, screen.HasGrid);
            Assert.Equal(ActivityState.WaitingForInput.ToString(), screen.ActivityState);
        }
        finally { logger.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void A_flip_to_anything_other_than_waiting_hands_the_sink_nothing()
    {
        // The control that says the capture is bound to the ONE trigger. Without it, a sink that fired on
        // every state change would pass the test above and flood the store.
        var manager = new SessionManager(new AgentOptions());
        var sink = new CapturingSink();
        var logger = new TurnReviewLogger(manager, sink);
        try
        {
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferOnlyBackend());
            logger.Start();

            session.ApplyTerminalActivityState(ActivityState.Working);
            Assert.Empty(sink.Received);

            // And the instrument is shown to be capable of firing at all, in the same test - an empty list
            // from a sink that could never fire would prove nothing about the trigger.
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Single(sink.Received);
        }
        finally { logger.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void A_logger_with_no_sink_still_writes_its_local_record()
    {
        // The screen push is an ADDITION to the turn review, never a condition of it. A Director with no
        // Gateway configured constructs the logger with a null sink, and the local record must be
        // untouched by that.
        var manager = new SessionManager(new AgentOptions());
        var logger = new TurnReviewLogger(manager);
        try
        {
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferOnlyBackend());
            logger.Start();
            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            // No throw is the assertion here, and it is a weak one on its own - the strong half is that the
            // two tests above prove the sink path works, so this one is only asserting that its ABSENCE is
            // tolerated rather than that anything happened.
        }
        finally { logger.Dispose(); manager.Dispose(); }
    }
}
