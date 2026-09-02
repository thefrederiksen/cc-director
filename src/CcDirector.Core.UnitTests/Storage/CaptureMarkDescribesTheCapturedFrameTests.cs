using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Storage;

/// <summary>
/// Inspection 01, finding 2: the terminal-screen capture must return a byte mark that describes THE FRAME
/// IT RETURNED, not some later state of the terminal.
///
/// WHY THIS IS NOT OBVIOUS FROM READING THE CODE. The terminal buffer increments its own total INSIDE its
/// write lock, releases that lock, and only THEN invokes <c>OnBytesWritten</c> - and the session's parser
/// is one of those subscribers. So between those two operations the buffer's total has already moved and
/// the parser still holds the previous frame. A mark taken from the buffer's total therefore OVERSTATES
/// the frame that comes back, and the comment on the capture claimed the opposite: that reading the
/// counter first could only ever understate it.
///
/// The fix takes the mark from the count of bytes the PARSER has consumed, read inside the same lock that
/// produces the rows, so the mark and the frame are one consistent observation. This test drives the exact
/// interleaving that made the old claim false: a subscriber ordered ahead of the session's parser holds a
/// second write open after the counter has advanced and before the parser sees it.
///
/// IT ASSERTS WHAT THE CAPTURE DID RETURN, both halves. The bad state is positively established first -
/// the buffer's total really has moved past the parser - and then the frame and the mark are checked
/// against each other. Releasing the writer and re-reading is the control: it shows the new bytes really
/// were on their way, so the first read was a genuine mid-flight observation and not a fixture that never
/// wrote anything.
/// </summary>
public class CaptureMarkDescribesTheCapturedFrameTests
{
    private const string OldFrame = "OLD_FRAME_MARKER";
    private const string NewFrame = "NEW_FRAME_MARKER";

    /// <summary>An in-process backend with a real terminal buffer that never spawns a process and never
    /// exits - the same stub the other capture tests keep.</summary>
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

    [Fact]
    public async Task The_mark_describes_the_frame_that_came_back_not_a_later_terminal()
    {
        var manager = new SessionManager(new AgentOptions());
        var backend = new BufferOnlyBackend();
        var buffer = backend.Buffer ?? throw new InvalidOperationException("the stub has no buffer");

        // Subscribed BEFORE the session, so this handler runs BEFORE the session's parser feed for every
        // write - which is what lets it hold a write open in exactly the window under test.
        var reached = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        // Armed through Interlocked rather than a plain bool: this flag is written on the test thread and
        // read on the writer thread, and it must fire for exactly ONE write. A plain read-then-assign would
        // be a data race in the instrument, which is the last place to leave one.
        var arm = 0;
        Action<byte[]> rendezvous = _ =>
        {
            if (Interlocked.CompareExchange(ref arm, 0, 1) != 1) return;
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        buffer.OnBytesWritten += rendezvous;

        try
        {
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);

            var oldBytes = Encoding.UTF8.GetBytes(OldFrame + "\r\n");
            var newBytes = Encoding.UTF8.GetBytes(NewFrame + "\r\n");

            // The first frame lands normally, parser and all.
            buffer.Write(oldBytes);
            var afterOld = buffer.TotalBytesWritten;
            Assert.Equal(oldBytes.Length, afterOld);

            // The second write is held between "the counter moved" and "the parser saw it".
            Interlocked.Exchange(ref arm, 1);
            var writer = Task.Run(() => buffer.Write(newBytes));
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)), "the rendezvous subscriber was never reached");

            // POSITIVELY establish the bad state before asserting anything about it: the buffer's total has
            // moved past the frame the parser holds. Without this the test could pass on a run where the
            // second write never happened at all.
            Assert.Equal(afterOld + newBytes.Length, buffer.TotalBytesWritten);

            var captured = session.SnapshotLiveScreenWithBufferMark();
            var rows = string.Join("\n", captured.Rows);
            Assert.Contains(OldFrame, rows);
            Assert.DoesNotContain(NewFrame, rows);

            // THE ASSERTION THIS TEST EXISTS FOR. The frame that came back reflects the first write only, so
            // the mark that came back with it must be the first write's byte position - never the buffer's
            // newer total, which describes a frame this capture did not return.
            Assert.Equal(afterOld, captured.BufferBytes);
            Assert.True(captured.BufferBytes < buffer.TotalBytesWritten,
                $"the mark ({captured.BufferBytes}) claimed bytes the returned frame does not reflect "
                + $"(the buffer holds {buffer.TotalBytesWritten})");

            // THE CONTROL. Release the writer: the new bytes really were in flight, so the next capture must
            // show them and the mark must advance with them. A fixture that never wrote anything fails here.
            release.Set();
            await writer;
            var after = session.SnapshotLiveScreenWithBufferMark();
            Assert.Contains(NewFrame, string.Join("\n", after.Rows));
            Assert.Equal(buffer.TotalBytesWritten, after.BufferBytes);
        }
        finally
        {
            release.Set();
            buffer.OnBytesWritten -= rendezvous;
            manager.Dispose();
            reached.Dispose();
            release.Dispose();
        }
    }
}
