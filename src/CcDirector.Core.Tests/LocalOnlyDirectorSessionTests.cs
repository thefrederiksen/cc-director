using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Regression proof for issue #1809: a freshly installed Director is fully usable with NO account and
/// NO gateway. This half pins the core "usable" claim - that opening a New Session does not depend on a
/// gateway or a signed-in account. A default <see cref="SessionManager"/> is exactly the local-only
/// Director state: no <see cref="SessionManager.FleetNumberSource"/> (the Gateway is not configured) and
/// no <see cref="SessionManager.SignedInUserAccessor"/> (no account). If session creation ever grew a
/// gateway/account dependency, these tests fail.
///
/// The companion first-run/onboarding half lives in <c>Onboarding/LocalOnlyFirstRunTests</c>. The
/// sessions here spawn a real stand-in shell (cmd.exe / /bin/sh via <see cref="TestShell"/>), matching
/// <see cref="SessionManagerTests"/>.
/// </summary>
public class LocalOnlyDirectorSessionTests : IDisposable
{
    private readonly SessionManager _manager;

    public LocalOnlyDirectorSessionTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = TestShell.Path, // cmd.exe on Windows, /bin/sh elsewhere
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        // A default SessionManager models a local-only Director: no Gateway wired (FleetNumberingActive
        // is false and FleetNumberSource is null) and no account (SignedInUserAccessor is null).
        _manager = new SessionManager(options);
    }

    [Fact]
    public void NewSession_WithNoGatewayAndNoAccount_StartsAndIsUsable()
    {
        // No account is signed in - make that explicit rather than relying on the null default.
        _manager.SignedInUserAccessor = () => null;

        var session = _manager.CreateSession(Path.GetTempPath());

        // The session runs locally with a live process - opening a New Session did not require a
        // gateway connection or a sign-in.
        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Running, session.Status);
        Assert.True(session.ProcessId > 0);
        Assert.Single(_manager.ListSessions());
    }

    [Fact]
    public void NewSession_WithNoGateway_GetsALocalOfflineNumberImmediately()
    {
        var session = _manager.CreateSession(Path.GetTempPath());

        // With no Gateway configured the number is assigned locally and synchronously, so a local-only
        // session is never left number-less waiting on a gateway that will never answer.
        Assert.NotNull(session.Number);
        Assert.InRange(session.Number!.Value, SessionNumberAllocator.MinNumber, SessionNumberAllocator.MaxNumber);
    }

    [Fact]
    public void NewSession_LocalOnly_NeverReachesOutToTheGatewayForANumber()
    {
        // Wire a Gateway number source but leave the Director in the local-only state (no gateway.url,
        // so FleetNumberingActive stays false). Creation must take the offline path and never consult
        // the Gateway - proving New Session makes no network/account call when there is no gateway.
        var gatewaySourceCalled = false;
        _manager.FleetNumberSource = (_, _) =>
        {
            gatewaySourceCalled = true;
            return Task.FromResult<int?>(123);
        };

        var session = _manager.CreateSession(Path.GetTempPath());

        Assert.False(gatewaySourceCalled);
        Assert.NotNull(session.Number);
        // The offline band (issue #1292) is the high end of the range; a local number never collides
        // with a coordinated Gateway hand-out from the low end.
        Assert.InRange(session.Number!.Value, SessionNumberAllocator.OfflineBandStart, SessionNumberAllocator.MaxNumber);
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
