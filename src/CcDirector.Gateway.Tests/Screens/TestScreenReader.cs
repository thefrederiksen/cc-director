using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Screens;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;

namespace CcDirector.Gateway.Tests.Screens;

/// <summary>
/// The <see cref="GatewayScreenReader"/> an endpoint-mapping test has to supply, and the throwaway database
/// behind it.
///
/// The reader is a REQUIRED, non-nullable argument to <c>GatewayEndpoints.Map</c> and
/// <c>GatewayWingmanVoiceEndpoint.Map</c> on purpose (Terminal Rules, issue #2644): it is the one place a
/// screen is read, and a defaulted null would have to mean "pull the tunnel anyway", which is a second
/// answer to a question that must have exactly one. So every host harness needs one, and building it by
/// hand in a dozen places would put a dozen slightly different constructions in the suite.
///
/// It is built over <see cref="GatewayDbTestHarness"/> rather than over a bare <c>GatewayDatabase</c> so it
/// costs one file copy instead of a full migration run per test - see that type's comment for the
/// measurement. The database is real and is disposed with the test.
/// </summary>
internal sealed class TestScreenReader : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    /// <summary>The pushed-snapshot store the reader reads liveness and the buffer mark from. Either the
    /// one the test already drives, or a private empty one when the test has none.</summary>
    public PushedSessionStore Pushed { get; }

    public GatewayScreenReader Reader { get; }

    public TestScreenReader(PushedSessionStore? pushed = null)
    {
        Pushed = pushed ?? new PushedSessionStore();
        Reader = new GatewayScreenReader(new SessionScreenStore(_harness.Open(new SingleTenantContext())), Pushed);
    }

    public void Dispose() => _harness.Dispose();

    /// <summary>The reader over a database the test ALREADY owns and already disposes - the several harnesses
    /// that hold a <see cref="GatewayDbTestHarness"/> for their own settings store need no second one.</summary>
    public static GatewayScreenReader Over(GatewayDatabase db, PushedSessionStore? pushed = null)
        => new(new SessionScreenStore(db), pushed ?? new PushedSessionStore());
}
