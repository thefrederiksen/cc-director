using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// HELLO IS THE ONLY THING THAT MAKES THIS LAUNCHER REACHABLE, SO IT HAS TO KEEP TRYING.
///
/// Inspection 3 raised this from the control flow and recorded it honestly as a hypothesis: Hello was
/// sent once and its failure swallowed with a note that auto-reconnect would retry - but retry only ever
/// happened on Reconnected. A hub or protocol error that fails Hello while SignalR stays CONNECTED fires
/// nothing, so nothing retries, and the launcher sits with an open stream, registered nowhere, every
/// command to it undeliverable, until a later disconnect that may never come.
///
/// It was then REPRODUCED against a real hub rather than argued about - see
/// <c>LauncherStreamIntegrationTests.A_failed_Hello_that_leaves_the_connection_up_registers_nothing_and_delivers_nothing</c>,
/// which drives a real SignalR client at the real LauncherHub, fails Hello at the protocol level, and
/// shows the connection still Connected with the machine undeliverable. These tests pin the answer to it.
///
/// They drive the retry loop directly with an injected sender and connection state. That is the whole
/// decision - keep trying while connected, stop when not - and driving it directly is what makes the
/// stopping condition testable at all: a test that waited on a real dropped connection would be timing,
/// not logic.
/// </summary>
public sealed class LauncherHelloRetryTests
{
    private static readonly TimeSpan Immediate = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// THE DEFECT'S EXACT SHAPE: Hello fails, the connection stays up, nothing external will ever nudge
    /// it. Before the fix that was permanent silence; now it is a delay.
    /// </summary>
    [Fact]
    public async Task Hello_keeps_being_sent_while_the_connection_stays_connected()
    {
        var attempts = 0;

        await LauncherStreamClient.SendHelloUntilAcceptedAsync(
            sendHello: () =>
            {
                attempts++;
                // Fails twice - a hub error, not a disconnect - then is accepted.
                return attempts < 3 ? Task.FromException(new InvalidOperationException("hub said no")) : Task.CompletedTask;
            },
            connectionState: () => HubConnectionState.Connected,
            disposed: () => false,
            retryDelay: Immediate,
            ct: CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    /// <summary>
    /// AND IT MUST NOT SPIN. When the connection is gone the reconnect path owns the case and will call
    /// this again on Reconnected, so retrying here would be two things racing to do one job - and a loop
    /// that kept calling into a dead connection would burn a core for as long as the outage lasted.
    /// </summary>
    [Fact]
    public async Task Hello_stops_being_retried_the_moment_the_connection_is_no_longer_connected()
    {
        var attempts = 0;
        var state = HubConnectionState.Connected;

        await LauncherStreamClient.SendHelloUntilAcceptedAsync(
            sendHello: () =>
            {
                attempts++;
                if (attempts == 2) state = HubConnectionState.Disconnected;
                return Task.FromException(new InvalidOperationException("hub said no"));
            },
            connectionState: () => state,
            disposed: () => false,
            retryDelay: Immediate,
            ct: CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    /// <summary>A shutting-down launcher stops immediately - it is not going to be reachable either way,
    /// and a disposing client must not be held open by a retry loop.</summary>
    [Fact]
    public async Task A_disposing_client_does_not_keep_retrying()
    {
        var attempts = 0;

        await LauncherStreamClient.SendHelloUntilAcceptedAsync(
            sendHello: () => { attempts++; return Task.CompletedTask; },
            connectionState: () => HubConnectionState.Connected,
            disposed: () => true,
            retryDelay: Immediate,
            ct: CancellationToken.None);

        Assert.Equal(0, attempts);
    }

    /// <summary>The ordinary case, stated so the retry cannot quietly become the normal path: one
    /// successful Hello is sent exactly once.</summary>
    [Fact]
    public async Task A_Hello_that_is_accepted_is_sent_once()
    {
        var attempts = 0;

        await LauncherStreamClient.SendHelloUntilAcceptedAsync(
            sendHello: () => { attempts++; return Task.CompletedTask; },
            connectionState: () => HubConnectionState.Connected,
            disposed: () => false,
            retryDelay: Immediate,
            ct: CancellationToken.None);

        Assert.Equal(1, attempts);
    }
}
