using CcDirector.Gateway.Stats;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Throttle;

/// <summary>
/// THE SUBSTRATE GUARD for ruling R9 of the "Clean up Your Throttle" mission: no count of turns on the
/// <c>GET /stats/data</c> feed may come from the <c>stat_delta</c> tally again.
///
/// Why a guard on the compiled code and not a test of the numbers. A test that seeds the tally with one
/// number and the ledger with another, and asserts the feed shows the ledger's, is a fine test of today's
/// wiring - and it is also exactly the test that stays green when somebody adds ONE MORE field to the feed
/// from the tally, because the test never asked about that field. The tally's turn-counting readers are a
/// closed set of methods on the aggregator. This reads every method body in <see cref="StatsPageEndpoint"/>
/// and its compiler-generated closures and fails on any call to one of them, whichever field it would have
/// fed. The aggregator's readers that count no turn - concurrency, token spend, the per-model spend split -
/// are not on the list, because the feed still legitimately serves those.
///
/// It is a known-BAD-input check as well: the list below is asserted to name REAL methods on the aggregator,
/// so a rename cannot leave this guard scanning for a name that no longer exists and passing on the absence.
/// </summary>
public sealed class ThrottleFeedNeverReadsTheTallyTests
{
    /// <summary>The aggregator methods that count turns from the stat_delta tally. Every one of them used to
    /// feed the page; none of them may again.</summary>
    private static readonly string[] TallyTurnReaders =
    {
        nameof(GatewayInputStatsAggregator.CurrentTotals),
        nameof(GatewayInputStatsAggregator.HourlyTurns),
        nameof(GatewayInputStatsAggregator.RepoTotals),
        nameof(GatewayInputStatsAggregator.AgentTotals),
        nameof(GatewayInputStatsAggregator.AgentsSinceUtc),
        nameof(GatewayInputStatsAggregator.WingmanUsage),
        nameof(GatewayInputStatsAggregator.AgentDrivenUsage),
    };

    [Fact]
    public void TheListNamesRealAggregatorMethods_SoARenameCannotBlindThisGuard()
    {
        var methods = typeof(GatewayInputStatsAggregator).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in TallyTurnReaders)
            Assert.Contains(name, methods);
    }

    [Fact]
    public void NothingInTheStatsFeedCallsATallyTurnReader()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CcDirector.Gateway.dll");
        Assert.True(File.Exists(path), $"expected {path} beside the tests");

        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var endpoint = assembly.MainModule.GetType(typeof(StatsPageEndpoint).FullName);
        Assert.NotNull(endpoint);

        var offenders = new List<string>();
        foreach (var type in SelfAndNested(endpoint!))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.FlowControl != FlowControl.Call) continue;
            if (instruction.Operand is not MethodReference callee) continue;
            if (callee.DeclaringType.FullName != typeof(GatewayInputStatsAggregator).FullName) continue;
            if (TallyTurnReaders.Contains(callee.Name, StringComparer.Ordinal))
                offenders.Add($"{type.FullName}.{method.Name} calls {callee.Name}");
        }

        Assert.True(offenders.Count == 0,
            "The stats feed reads a count of TURNS from the stat_delta tally again. Every turn figure comes " +
            "from the submission ledger through ThrottleDefinition (ruling R9); a tally-derived turn count on " +
            "this feed is the 92-per-cent defect coming back. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The feed DOES still read the readers that count no turn - asserted so this guard is proven
    /// to be looking at the real call sites rather than at a type whose lambdas Cecil failed to reach.</summary>
    [Fact]
    public void TheGuardSeesTheFeedsRealCallSites()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CcDirector.Gateway.dll");
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var endpoint = assembly.MainModule.GetType(typeof(StatsPageEndpoint).FullName)!;

        var calls = SelfAndNested(endpoint)
            .SelectMany(t => t.Methods.Where(m => m.HasBody))
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode.FlowControl == FlowControl.Call && i.Operand is MethodReference)
            .Select(i => (MethodReference)i.Operand)
            .Select(c => $"{c.DeclaringType.Name}.{c.Name}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ThrottleLedgerReader.Compute", calls);
        Assert.Contains("GatewayInputStatsAggregator.TokenSpend", calls);
    }

    private static IEnumerable<TypeDefinition> SelfAndNested(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
        foreach (var inner in SelfAndNested(nested))
            yield return inner;
    }
}
