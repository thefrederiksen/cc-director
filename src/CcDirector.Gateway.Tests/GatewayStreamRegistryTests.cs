using System.Runtime.CompilerServices;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (up-stream): acceptance tests for <see cref="GatewayStreamRegistry"/> -
/// the Gateway's receive side of the tunnel up-stream. Covers the three Architect refinements that live in the
/// registry: pull-then-forward backpressure (ruling 1), and the two close-stream lifecycle races plus the
/// StreamUp-never-arrives timeout (ruling 3). Framing order and completion are covered too.
/// </summary>
public sealed class GatewayStreamRegistryTests
{
    // A sink that records the frames it is handed, optionally blocking each write on a supplied gate so a test
    // can hold the pull and observe backpressure.
    private sealed class RecordingSink : IStreamSink
    {
        private readonly Func<Task>? _gate;
        public RecordingSink(Func<Task>? gate = null) => _gate = gate;
        public List<DirectorStreamFrame> Frames { get; } = new();
        public bool Completed { get; private set; }
        public string? CompletedReason { get; private set; }

        public async Task WriteFrameAsync(DirectorStreamFrame frame, CancellationToken cancellationToken)
        {
            if (_gate is not null) await _gate();
            Frames.Add(frame);
        }

        public Task CompleteAsync(string? reason)
        {
            Completed = true;
            CompletedReason = reason;
            return Task.CompletedTask;
        }
    }

    private static DirectorStreamFrame Size(string s) => new() { StreamId = s, Kind = DirectorStreamFrameType.Size, Cols = 80, Rows = 24 };
    private static DirectorStreamFrame Bin(string s, byte b) => new() { StreamId = s, Kind = DirectorStreamFrameType.Binary, Data = new[] { b } };
    private static DirectorStreamFrame Closed(string s, string reason) => new() { StreamId = s, Kind = DirectorStreamFrameType.Closed, Reason = reason };

    private static async IAsyncEnumerable<DirectorStreamFrame> SizeBinaryClosed(string s, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return Size(s);
        yield return Bin(s, 1);
        await Task.Yield();
        yield return Closed(s, "eof");
    }

    [Fact]
    public async Task ConsumeAsync_RoutesFramesToSinkInOrder_ThenTearsDown()
    {
        var registry = new GatewayStreamRegistry();
        var sink = new RecordingSink();
        registry.Register("s1", sink);

        await registry.ConsumeAsync("s1", SizeBinaryClosed("s1"), CancellationToken.None);

        Assert.Equal(3, sink.Frames.Count);
        Assert.Equal(DirectorStreamFrameType.Size, sink.Frames[0].Kind);
        Assert.Equal(DirectorStreamFrameType.Binary, sink.Frames[1].Kind);
        Assert.Equal(DirectorStreamFrameType.Closed, sink.Frames[2].Kind);
        Assert.True(sink.Completed);
        Assert.Equal("eof", sink.CompletedReason);
        Assert.Equal(0, registry.LiveStreamCount); // torn down after completion
    }

    [Fact]
    public async Task ConsumeAsync_IsPullThenForward_BackpressuresTheProducer()
    {
        // The backpressure invariant (ruling 1): the registry must await the sink write of one frame BEFORE it
        // pulls the next frame. A sink whose write is held therefore stops the producer from running ahead.
        var registry = new GatewayStreamRegistry();
        var gate = new TaskCompletionSource();
        var sink = new RecordingSink(gate: () => gate.Task);
        registry.Register("bp", sink);

        var yielded = 0;
        async IAsyncEnumerable<DirectorStreamFrame> Produce([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < 5; i++)
            {
                Interlocked.Increment(ref yielded);
                yield return Bin("bp", (byte)i);
                await Task.Yield();
            }
            yield return Closed("bp", "eof");
        }

        var consume = registry.ConsumeAsync("bp", Produce(), CancellationToken.None);
        await Task.Delay(150); // let the consumer pull the first frame and block on the held sink write

        Assert.Equal(1, Volatile.Read(ref yielded)); // only the first frame was pulled; the next pull is blocked
        Assert.False(consume.IsCompleted);

        gate.SetResult(); // release the sink writes
        await consume;

        Assert.Equal(5, Volatile.Read(ref yielded)); // all five binaries were pulled (the counter counts binaries)
        Assert.Equal(6, sink.Frames.Count); // and the sink received all five binaries plus the closed frame
    }

    [Fact]
    public async Task ConsumeAsync_CloseCancelsTheStream_EndsTheEnumerableAndTearsDown()
    {
        var registry = new GatewayStreamRegistry();
        var sink = new RecordingSink();
        registry.Register("inf", sink);

        var started = new TaskCompletionSource();
        async IAsyncEnumerable<DirectorStreamFrame> Forever([EnumeratorCancellation] CancellationToken ct = default)
        {
            started.TrySetResult();
            var i = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                yield return Bin("inf", (byte)(i++ & 0xFF));
                await Task.Delay(20, ct);
            }
        }

        var consume = registry.ConsumeAsync("inf", Forever(), CancellationToken.None);
        await started.Task;
        await Task.Delay(60); // let a few frames flow

        registry.Close("inf"); // browser disconnected
        await consume; // completes cleanly (cancellation swallowed), does not throw

        Assert.True(sink.Completed);
        Assert.Equal(0, registry.LiveStreamCount);
    }

    [Fact]
    public async Task ConsumeAsync_ForAStreamWhoseSinkIsGone_DropsImmediately()
    {
        // StreamUp-after-sink-gone (ruling 3): the browser disconnected before the frames arrived; there is no
        // registered sink, so consume returns at once without touching any sink.
        var registry = new GatewayStreamRegistry();
        var sink = new RecordingSink();
        registry.Register("gone", sink);
        registry.Close("gone"); // sink torn down before StreamUp arrives

        await registry.ConsumeAsync("gone", SizeBinaryClosed("gone"), CancellationToken.None);

        Assert.Empty(sink.Frames); // nothing was pumped into the gone sink
        Assert.Equal(0, registry.LiveStreamCount);
    }

    [Fact]
    public async Task Register_WhenStreamUpNeverArrives_TearsTheSinkDownAfterTheTimeout()
    {
        // StreamUp-never-arrives (ruling 3): the Director died mid-open, so no StreamUp ever comes; the sink
        // must not wait forever - the open timeout tears it down and fires the caller's token.
        var registry = new GatewayStreamRegistry(openTimeout: TimeSpan.FromMilliseconds(100));
        var sink = new RecordingSink();
        var token = registry.Register("late", sink);

        await Task.Delay(400);

        Assert.True(sink.Completed);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(0, registry.LiveStreamCount);
    }

    [Fact]
    public void Register_DuplicateStreamId_ThrowsFailLoud()
    {
        var registry = new GatewayStreamRegistry();
        registry.Register("dup", new RecordingSink());
        Assert.Throws<InvalidOperationException>(() => registry.Register("dup", new RecordingSink()));
    }
}
