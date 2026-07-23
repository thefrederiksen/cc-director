using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="LauncherCommandRouter"/> (launcher-persistent-join), the launcher twin of the
/// Director command router. It is the ONE place the Gateway decides "push down the launcher stream, or fall
/// back to the HTTP relay". A null return is the fall-back signal, for BOTH the flag-off case (the send
/// delegate is null) and the flag-on-but-launcher-offline case (the delegate returns null); a non-null result
/// - success OR a typed failure - is authoritative.
/// </summary>
public sealed class LauncherCommandRouterTests
{
    private static LauncherCommand StartCommand() => new() { Verb = "director/start" };

    [Fact]
    public async Task TrySendAsync_NullDelegate_ReturnsNull_ForRestFallback()
    {
        // Arrange + Act - stream mode off: the host passes a null send delegate.
        var result = await LauncherCommandRouter.TrySendAsync(null, CcDirector.Core.Tenancy.TenantId.Local, "machine-A", StartCommand(), CancellationToken.None);

        // Assert - null tells the caller to use the existing HTTP relay.
        Assert.Null(result);
    }

    [Fact]
    public async Task TrySendAsync_DelegateReturnsResult_ReturnsThatResult()
    {
        // Arrange - the launcher is online: the delegate returns an authoritative result.
        var expected = LauncherCommandResult.Ok();
        LauncherCommandRouter.SendLauncherCommandAsync del = (_, _, _, _) => Task.FromResult<LauncherCommandResult?>(expected);

        // Act
        var result = await LauncherCommandRouter.TrySendAsync(del, CcDirector.Core.Tenancy.TenantId.Local, "machine-A", StartCommand(), CancellationToken.None);

        // Assert - the stream result is returned unchanged; the caller must NOT also relay over HTTP.
        Assert.Same(expected, result);
    }

    [Fact]
    public async Task TrySendAsync_DelegateReturnsNull_ReturnsNull_ForRestFallback()
    {
        // Arrange - stream mode on but the launcher has no active connection: the delegate returns null.
        LauncherCommandRouter.SendLauncherCommandAsync del = (_, _, _, _) => Task.FromResult<LauncherCommandResult?>(null);

        // Act
        var result = await LauncherCommandRouter.TrySendAsync(del, CcDirector.Core.Tenancy.TenantId.Local, "machine-A", StartCommand(), CancellationToken.None);

        // Assert - still the HTTP-relay fall-back signal.
        Assert.Null(result);
    }

    [Fact]
    public async Task TrySendAsync_PassesMachineNameAndCommand_ToTheDelegate()
    {
        // Arrange
        string? seenMachine = null;
        string? seenVerb = null;
        LauncherCommandRouter.SendLauncherCommandAsync del = (_, machine, cmd, _) =>
        {
            seenMachine = machine;
            seenVerb = cmd.Verb;
            return Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.Ok());
        };

        // Act
        await LauncherCommandRouter.TrySendAsync(del, CcDirector.Core.Tenancy.TenantId.Local, "machine-Z", new LauncherCommand { Verb = "director/restart" }, CancellationToken.None);

        // Assert - the router forwards the routing key and command verbatim.
        Assert.Equal("machine-Z", seenMachine);
        Assert.Equal("director/restart", seenVerb);
    }

    [Fact]
    public async Task TrySendAsync_DelegateReturnsTypedFailure_IsReturnedAuthoritatively()
    {
        // Arrange - the launcher rejected the command; a typed failure is authoritative, not a fall-back.
        var failure = LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, "unknown verb: bogus");
        LauncherCommandRouter.SendLauncherCommandAsync del = (_, _, _, _) => Task.FromResult<LauncherCommandResult?>(failure);

        // Act
        var result = await LauncherCommandRouter.TrySendAsync(del, CcDirector.Core.Tenancy.TenantId.Local, "machine-A", new LauncherCommand { Verb = "bogus" }, CancellationToken.None);

        // Assert - a non-null failure is returned as-is (the caller must NOT relay over HTTP).
        Assert.Same(failure, result);
        Assert.NotNull(result);
        Assert.False(result!.IsOk);
    }
}

/// <summary>
/// Unit tests for the <see cref="LauncherCommandResult"/> factory helpers (launcher-persistent-join).
/// </summary>
public sealed class LauncherCommandResultTests
{
    [Fact]
    public void Ok_ProducesOkStatus_WithNoError()
    {
        var result = LauncherCommandResult.Ok();

        Assert.Equal(LauncherCommandStatus.Ok, result.Status);
        Assert.Null(result.Error);
        Assert.True(result.IsOk);
    }

    [Fact]
    public void Fail_ProducesTheGivenStatusAndMessage()
    {
        var result = LauncherCommandResult.Fail(LauncherCommandStatus.Error, "boom");

        Assert.Equal(LauncherCommandStatus.Error, result.Status);
        Assert.Equal("boom", result.Error);
        Assert.False(result.IsOk);
    }

    [Fact]
    public void Fail_WithBadRequest_IsNotOk()
    {
        var result = LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, "path is required for launch");

        Assert.Equal(LauncherCommandStatus.BadRequest, result.Status);
        Assert.Equal("path is required for launch", result.Error);
        Assert.False(result.IsOk);
    }
}
