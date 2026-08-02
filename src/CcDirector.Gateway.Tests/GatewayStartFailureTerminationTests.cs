using System.Reflection;
using CcDirector.Gateway;
using CcDirector.Gateway.Data;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The 2 August outage fix (issue #2383), guarded at the two places it actually lives.
///
/// WHAT WENT WRONG. GatewayService.StartAsync catches every startup exception, logs it, and does NOT
/// rethrow. GatewayWorker then awaited Task.Delay(Timeout.Infinite). So a Gateway that could not open its
/// database stayed ALIVE with nothing listening; the platform waited out its 230-second container start
/// limit, found no port, and STOPPED THE SITE - killing the healthy container serving beside it. The fix is
/// not a retry. It is that the PROCESS must end, because a container that exits gets restarted and a
/// container that is alive and silent gets waited out.
///
/// WHY THESE TESTS AND NOT THE PREVIOUS ONES. The first version of this file tested pure helpers only. The
/// reviewer reverted the real call sites - the two UseNpgsql lines and the DescribeFailure call - and every
/// test stayed green. Those tests were decoration. Each test below was checked by mutating the exact
/// production LINE it claims to guard, confirming by diff that the mutation was really in the file, and
/// requiring a RED before the mutation was reverted.
/// </summary>
public sealed class GatewayStartFailureTerminationTests
{
    // ---- the termination policy ------------------------------------------------------------------
    // Guards: GatewayWorker.MustTerminate. Mutating it to `=> false` reddens FailedMustEndTheProcess.

    [Fact]
    public void AFailedStart_MustEndTheProcess()
    {
        Assert.True(GatewayWorker.MustTerminate(GatewayServiceState.Failed));
    }

    [Theory]
    [InlineData(GatewayServiceState.Running)]
    [InlineData(GatewayServiceState.Starting)]
    [InlineData(GatewayServiceState.Stopped)]
    public void AnyOtherState_MustNotEndTheProcess(GatewayServiceState state)
    {
        // A Gateway that started must never be killed by this rule, and a deliberate stop is not a failure.
        Assert.False(GatewayWorker.MustTerminate(state));
    }

    [Fact]
    public void TheFailureExitCode_IsNonZero()
    {
        // The platform decides what to do from the exit code; zero reads as a clean, intended shutdown,
        // which is exactly the wrong signal for a Gateway that could not start.
        Assert.NotEqual(0, GatewayWorker.StartFailureExitCode);
    }

    // ---- the termination WIRING ------------------------------------------------------------------
    // The policy above is worthless if nothing calls it, and that call sits inside an async state machine
    // that cannot be invoked from a test without ending the test run. So the wiring is read out of the
    // compiled IL: ExecuteAsync's state machine must call BOTH MustTerminate and Environment.Exit.
    // Deleting either from GatewayWorker reddens this.

    [Fact]
    public void ExecuteAsync_ConsultsThePolicyAndEndsTheProcess()
    {
        var module = ModuleDefinition.ReadModule(typeof(GatewayWorker).Assembly.Location);
        var worker = module.GetType(typeof(GatewayWorker).FullName);
        Assert.NotNull(worker);

        // The async body is compiled into a nested state machine; scan the worker type and its nested types.
        var calls = new List<string>();
        foreach (var type in new[] { worker }.Concat(worker!.NestedTypes))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                    {
                        if (instruction.Operand is MethodReference called)
                            calls.Add($"{called.DeclaringType.FullName}::{called.Name}");
                    }
                }
            }
        }

        Assert.Contains(calls, c => c.EndsWith("GatewayWorker::MustTerminate"));
        Assert.Contains(calls, c => c == "System.Environment::Exit");
    }

    // ---- wiring guards for the sites the previous tests left unguarded ---------------------------
    // The reviewer reverted the two UseNpgsql lines and the DescribeFailure call and every test stayed
    // green. These close the DELETION case at both sites by reading the compiled IL.
    //
    // WHAT THESE DO NOT CATCH, stated plainly rather than implied: they prove the bounding helper and the
    // redacting describer are CALLED on those paths. They cannot prove which value reaches UseNpgsql, so a
    // mutation that keeps the call and passes the raw connection string instead of the bounded one stays
    // green. That is a dataflow property, and proving it needs an integration test against a real server
    // (PostgresProviderProofTests is where such a proof belongs) rather than a metadata scan. Claiming
    // otherwise is what made the first version of this file decoration.

    private static List<string> CallsIn(Type type, string? methodName = null)
    {
        var module = ModuleDefinition.ReadModule(type.Assembly.Location);
        var target = module.GetType(type.FullName);
        Assert.NotNull(target);
        var calls = new List<string>();
        foreach (var t in new[] { target! }.Concat(target!.NestedTypes))
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                if (methodName is not null && !m.Name.Contains(methodName) && !t.Name.Contains(methodName)) continue;
                foreach (var i in m.Body.Instructions)
                {
                    if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                        && i.Operand is MethodReference r)
                        calls.Add($"{r.DeclaringType.Name}::{r.Name}");
                }
            }
        }
        return calls;
    }

    [Fact]
    public void TheGatewayStore_BoundsItsPoolAndRedactsItsFailures()
    {
        var calls = CallsIn(typeof(GatewayDatabase));
        Assert.Contains(calls, c => c == "GatewayDatabase::WithBoundedPool");
        Assert.Contains(calls, c => c == "GatewayDatabase::DescribeFailure");
    }

    [Fact]
    public void TheStatisticsStore_BoundsItsPoolToo()
    {
        // The second Npgsql pool in every container - the one that roughly doubled the connection demand
        // and turned a slow post-swap boot into a refused one.
        var statsStore = typeof(GatewayDatabase).Assembly.GetType("CcDirector.Gateway.Stats.Data.GatewayStatsStore");
        Assert.NotNull(statsStore);
        Assert.Contains(CallsIn(statsStore!), c => c == "GatewayDatabase::WithBoundedPool");
    }

}

/// <summary>
/// The redaction boundary around the connection-string parse, in its own class because it SETS the
/// process-wide <c>CC_GATEWAY_DB_CONNECTION</c>. It joins the collection that exists for exactly that
/// hazard: a concurrent test constructing a GatewayDatabase would otherwise read this test's value
/// mid-flight and select the wrong provider. The value is also saved and restored in a finally, as that
/// collection's own tests do.
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class GatewayConnectionStringRedactionTests
{
    // ---- the redaction boundary around the connection-string parse -------------------------------
    // Guards the REAL production line: GatewayDatabase's constructor calling WithBoundedPool inside a
    // redacting try/catch. Removing that try/catch reddens this, because Npgsql's own message carries the
    // offending keyword and would reach the caller - and GatewayService.StartAsync writes ex.Message
    // straight to disk.
    //
    // The secret is placed in KEYWORD position because that is the position Npgsql echoes. Measured:
    // "SUPERSECRET=x" throws "Couldn't set supersecret (Parameter 'supersecret')". The value position does
    // not echo, so a test using it would pass whether or not the boundary existed.

    [Fact]
    public void AnUnparseableConnectionString_NeverPutsItsTextInTheError()
    {
        const string secret = "hunter2secretfragment";
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, $"{secret}=x");

            var ex = Assert.ThrowsAny<Exception>(
                () => new GatewayDatabase(new CcDirector.Core.Tenancy.SingleTenantContext()));

            // Case-INSENSITIVE: Npgsql lowercases the keyword it echoes, so a case-sensitive check would
            // pass while the secret sat in the log. That is how this leak was nearly missed.
            Assert.DoesNotContain(secret, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
        }
    }
}
