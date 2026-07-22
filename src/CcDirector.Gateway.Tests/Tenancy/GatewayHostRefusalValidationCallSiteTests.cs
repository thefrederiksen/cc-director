using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CcDirector.Gateway;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// Revert-proof for the PRODUCTION CALL SITE, not the helper. The pre-start fail-before-start harness
/// (<see cref="HostedRefusalRouteSpaceFailBeforeStartTests"/>) proves the SELECTION and the VALIDATOR
/// are correct, but it reaches them by calling <c>SelectFinalisedEndpoints</c> / <c>Validate</c> itself -
/// it never drives <see cref="GatewayHost"/>. So reverting only the production call at
/// <c>GatewayHost.StartAsync</c> (the <c>ValidateBeforeStart(_app)</c> line, back to the old empty-DI
/// <c>EndpointDataSource</c> read) changes nothing that harness reaches; the missing route-space
/// validation would ship silently.
///
/// No production family adopts the refusal primitive yet, so a really-started GatewayHost has zero refusal
/// endpoints and the validator early-returns - there is no conflicting final route set to inject through the
/// public surface, and production is locked, so a behavioural pre-listener-throw test on the real host is not
/// reachable. What IS reachable, and is exactly the guarantee the call site must keep, is that the COMPILED
/// <c>StartAsync</c> invokes <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/> at all. This guard
/// inspects the compiled body and fails the moment GatewayHost bypasses that call. The revert-proof: reverting
/// <c>GatewayHost.StartAsync</c>'s <c>ValidateBeforeStart(_app)</c> to the old DI-composite read removes the
/// call and reddens this test.
/// </summary>
public sealed class GatewayHostRefusalValidationCallSiteTests
{
    [Fact]
    public void GatewayHost_StartAsync_invokes_ValidateBeforeStart_on_the_finalised_route_space()
    {
        var calls = CalledMethodsIn(StartAsyncBody());

        var validateBeforeStart = calls.FirstOrDefault(m =>
            m.DeclaringType == typeof(HostedRefusalRouteSpace) &&
            m.Name == nameof(HostedRefusalRouteSpace.ValidateBeforeStart));

        Assert.True(validateBeforeStart is not null,
            "GatewayHost.StartAsync must call HostedRefusalRouteSpace.ValidateBeforeStart before it binds a " +
            "listener; the call is absent from the compiled method, so the finalised route space is not " +
            "validated at the production call site.");
    }

    [Fact]
    public void GatewayHost_StartAsync_validates_the_route_space_before_it_starts_the_app()
    {
        var (il, module, typeArgs, methodArgs) = StartAsyncBody();
        var callSites = CallSitesIn(il, module, typeArgs, methodArgs);

        var validateOffset = callSites
            .Where(c => c.Method.DeclaringType == typeof(HostedRefusalRouteSpace) &&
                        c.Method.Name == nameof(HostedRefusalRouteSpace.ValidateBeforeStart))
            .Select(c => (int?)c.Offset)
            .FirstOrDefault();

        var startAppOffset = callSites
            .Where(c => c.Method.DeclaringType == typeof(WebApplication) &&
                        c.Method.Name == nameof(WebApplication.StartAsync))
            .Select(c => (int?)c.Offset)
            .FirstOrDefault();

        Assert.True(validateOffset is not null, "GatewayHost.StartAsync must call ValidateBeforeStart.");
        Assert.True(startAppOffset is not null, "GatewayHost.StartAsync must call WebApplication.StartAsync.");
        Assert.True(validateOffset < startAppOffset,
            "GatewayHost.StartAsync must validate the finalised route space BEFORE it starts the app and binds " +
            "a listener, so a hosted-refusal conflict fails the start rather than surfacing as a request-time " +
            $"failure. ValidateBeforeStart is at IL offset {validateOffset}, StartAsync at {startAppOffset}.");
    }

    // ---- compiled-body inspection ----

    /// <summary>
    /// GatewayHost.StartAsync is async, so its real body is the compiler-generated state machine's MoveNext.
    /// Returns that MoveNext IL plus the generic context needed to resolve its metadata tokens (both empty
    /// here - StartAsync and its state machine are non-generic).
    /// </summary>
    private static (byte[] Il, Module Module, Type[] TypeArgs, Type[] MethodArgs) StartAsyncBody()
    {
        var startAsync = typeof(GatewayHost).GetMethod(
            nameof(GatewayHost.StartAsync), BindingFlags.Public | BindingFlags.Instance);
        Assert.True(startAsync is not null, "GatewayHost.StartAsync must exist.");

        var stateMachine = startAsync!.GetCustomAttribute<AsyncStateMachineAttribute>();
        Assert.True(stateMachine is not null, "GatewayHost.StartAsync must be async.");

        var moveNext = stateMachine!.StateMachineType.GetMethod(
            "MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(moveNext is not null, "The StartAsync state machine must have a MoveNext.");

        var body = moveNext!.GetMethodBody();
        Assert.True(body is not null, "MoveNext must have an IL body.");

        return (
            body!.GetILAsByteArray()!,
            moveNext.Module,
            moveNext.DeclaringType!.GetGenericArguments(),
            moveNext.GetGenericArguments());
    }

    private static IReadOnlyList<MethodBase> CalledMethodsIn(
        (byte[] Il, Module Module, Type[] TypeArgs, Type[] MethodArgs) body)
        => CallSitesIn(body.Il, body.Module, body.TypeArgs, body.MethodArgs).Select(c => c.Method).ToList();

    /// <summary>
    /// Walks the IL, resolving every method-token operand (call, callvirt, newobj, ldftn, ...) to the method
    /// it targets, paired with the byte offset of its opcode so callers can reason about ordering. Operand
    /// sizes come from the framework's own <see cref="OpCodes"/> table, so the walk cannot mistake an operand
    /// byte for an opcode.
    /// </summary>
    private static IReadOnlyList<(MethodBase Method, int Offset)> CallSitesIn(
        byte[] il, Module module, Type[] typeArgs, Type[] methodArgs)
    {
        var opcodes = OpcodeTable();
        var result = new List<(MethodBase, int)>();
        var pos = 0;

        while (pos < il.Length)
        {
            var opcodeOffset = pos;
            short key = il[pos];
            if (il[pos] == 0xFE)
            {
                key = unchecked((short)(0xFE00 | il[pos + 1]));
                pos += 2;
            }
            else
            {
                pos += 1;
            }

            Assert.True(opcodes.TryGetValue(key, out var opcode),
                $"Unknown IL opcode 0x{key:X} at offset {opcodeOffset} in GatewayHost.StartAsync MoveNext.");

            if (opcode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, pos);
                MethodBase? method = null;
                try { method = module.ResolveMethod(token, typeArgs, methodArgs); }
                catch (ArgumentException) { /* not a method token we can resolve; not our call site */ }
                if (method is not null) result.Add((method, opcodeOffset));
            }

            pos += OperandSize(opcode, il, pos);
        }

        return result;
    }

    private static int OperandSize(OpCode opcode, byte[] il, int operandPos) => opcode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
        OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
        OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operandPos)),
        _ => throw new InvalidOperationException($"Unhandled operand type {opcode.OperandType}."),
    };

    private static Dictionary<short, OpCode> OpcodeTable()
    {
        var table = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opcode)
            {
                table[opcode.Value] = opcode;
            }
        }
        return table;
    }
}
