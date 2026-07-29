#nullable enable

using Xunit;

namespace CcDirector.TestInfrastructure;

/// <summary>
/// The per-assembly sentinel: proves the mutation-proof pin guard's module initializer actually RAN in
/// this test process.
///
/// WHY THIS IS SEPARATE FROM THE REST OF THE GUARD'S TESTS.
///
/// The guard is linked into every test assembly, so every assembly needs its own proof that the mechanism
/// fired there - a module initializer that silently does not run leaves no trace at all, and the assembly
/// would go back to admitting contaminated proofs with every other test still green.
///
/// But the guard's BEHAVIOURAL tests do not need to run seven times. They build real git repositories and
/// launch real processes, and duplicating that into six assemblies buys no coverage while adding load and
/// changing test scheduling in assemblies that have nothing to do with this mechanism. One of those
/// assemblies contains tests that read Avalonia styled properties, which throw unless they run on the
/// dispatcher thread; a continuous-integration run reddened there on a head where the only change was to
/// this guard's own tests. That was not traced to a cause and may be unrelated - but a mechanism has no
/// business changing the conditions other suites run under, and the cheapest way to stop being a suspect
/// is to stop being present.
///
/// So: the GUARD goes everywhere, because that is the mechanism. This SENTINEL goes everywhere, because
/// each assembly must prove its own copy woke up. The behavioural suite lives in one assembly.
/// </summary>
public sealed class MutationProofPinGuardArmedTests
{
    [Fact]
    public void TheGuardRanInThisProcess()
    {
        Assert.True(
            MutationProofPinGuard.HasRun,
            "The mutation-proof pin guard's module initializer did not run in this test process, so "
            + "nothing checked whether this run's working tree is the tree a proof pinned. Every other "
            + "test in this assembly would still pass. See MutationProofPinGuard.");

        Assert.NotEqual("(the guard has not run)", MutationProofPinGuard.LastVerdictSummary);
    }
}
