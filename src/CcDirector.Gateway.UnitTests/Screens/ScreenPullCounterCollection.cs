using Xunit;

namespace CcDirector.Gateway.UnitTests.Screens;

/// <summary>
/// Serialises every test that reads <c>SessionVerbClient.ScreenGridPulls</c>.
///
/// That counter is PROCESS-WIDE on purpose - it counts the tunnel send itself rather than any caller, so
/// a caller added later is counted too, which is the property the phase 0 proof needs. The cost of that
/// choice is that a before-and-after difference is only meaningful while nothing else in the process is
/// pulling a screen. This assembly runs its collections in parallel (see AssemblyParallelism.cs, where the
/// thread cap is load-bearing), so without this collection two screen tests could interleave and each
/// would read the other's pulls into its own difference - an intermittent red that passes in isolation,
/// which is the worst kind.
///
/// Membership is exactly the classes that touch that counter. If a new one is written, it belongs here.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScreenPullCounterCollection
{
    public const string Name = "screen-pull-counter";
}
