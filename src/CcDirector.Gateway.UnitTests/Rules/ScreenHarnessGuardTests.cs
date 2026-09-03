using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE HARNESS GOES THROUGH THE REAL ENGINE, AND THIS IS WHAT PROVES IT (Session Rules mission, phase 0).
/// The acceptance row most likely to be quietly broken is "runs against the real engine, not a copy": a
/// runner that built its own prompt, or read the reply itself, or posted to the model over its own HTTP
/// client would prove something about the runner and nothing about the engine. So the built
/// <c>CcDirector.Rules.ScreenHarness.dll</c> is read with Mono.Cecil, as <c>RulesTypeNothingGuardTests</c>
/// and <c>NoListenerDependencyGuardTests</c> read theirs, and three things are asserted against its metadata:
///
///  - it CALLS <c>RuleEvaluator::EvaluateAsync</c> - the evidence it goes through the engine;
///  - it holds NO call to <c>RuleAgentContract::BuildPrompt</c> or <c>::Read</c> and no string literal
///    containing the screen delimiter the real prompt uses - the evidence it is not a second implementation
///    of the question;
///  - it holds NO call to any <c>HttpClient</c> send, post or get - the evidence its only model call is the
///    real <c>HostedInferenceBrain</c>.
///
/// EVERY ABSENCE IS PAIRED WITH A PRESENCE THE SAME SCANNER MUST FIND. A scanner that found nothing would
/// pass every "no call to X" below whether or not it was reading anything, so before any absence is trusted
/// the scanner is pointed at a call that is really there (the evaluator call, the brain's constructor, a
/// string literal the harness really holds) and at the Gateway assembly, where the hosted brain really does
/// call <c>HttpClient::SendAsync</c>, and required to find them. If an instrument check comes back empty
/// the scanner is broken, and every clean result below it means nothing.
///
/// Compiler-generated nested types are included: an async method's body lives in a state machine nested
/// under its declaring type, and that is where every call this guard is about actually sits.
/// </summary>
public sealed class ScreenHarnessGuardTests
{
    private const string HarnessAssemblyFile = "CcDirector.Rules.ScreenHarness.dll";

    /// <summary>The engine's entry point. The harness must call it.</summary>
    private const string TheEvaluatorCall = "CcDirector.Gateway.Rules.RuleEvaluator::EvaluateAsync";

    /// <summary>The real model call. The harness must construct it and nothing else that talks HTTP.</summary>
    private const string TheBrainConstructor = "CcDirector.Gateway.Wingman.HostedInferenceBrain::.ctor";

    /// <summary>The two halves of the question the harness must never implement itself.</summary>
    private static readonly string[] TheQuestionsOwnMethods =
    {
        "CcDirector.Gateway.Rules.RuleAgentContract::BuildPrompt",
        "CcDirector.Gateway.Rules.RuleAgentContract::Read",
    };

    /// <summary>The delimiter the real prompt puts around the screen. A second prompt builder would have to
    /// write it, or something like it, and this is the one the engine writes.</summary>
    private const string TheScreenDelimiter = "--- the session's screen ---";

    /// <summary>A string the harness really holds, so the literal scanner can be proven on a positive.</summary>
    private const string ALiteralTheHarnessHolds = "the harness was asked to type";

    /// <summary>The HTTP sends a hand-rolled model call would have to make. Matched by METHOD NAME on any
    /// declaring type under <c>System.Net.Http</c>, because <c>PostAsJsonAsync</c> is an extension method
    /// declared on <c>HttpClientJsonExtensions</c> rather than on <c>HttpClient</c> itself, and a guard that
    /// named the class would miss exactly the convenient overload somebody would reach for.</summary>
    private static readonly string[] HttpSends = { "SendAsync", "PostAsync", "PostAsJsonAsync", "GetAsync", "GetStringAsync" };

    private const string HttpNamespace = "System.Net.Http";

    private static readonly Lazy<ModuleDefinition> HarnessModule = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, HarnessAssemblyFile);
        Assert.True(File.Exists(path), "the screen harness assembly is not beside the tests at " + path +
                                       " - the unit-test project must reference the harness project.");
        return ModuleDefinition.ReadModule(path);
    }, isThreadSafe: true);

    /// <summary>Every type in a module, nested ones included.</summary>
    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in Nested(type)) yield return nested;
        }

        static IEnumerable<TypeDefinition> Nested(TypeDefinition type)
        {
            foreach (var nested in type.NestedTypes)
            {
                yield return nested;
                foreach (var deeper in Nested(nested)) yield return deeper;
            }
        }
    }

    /// <summary>Every method body in the module, with the outermost type it belongs to.</summary>
    private static IEnumerable<(string Where, MethodDefinition Method)> AllBodies(ModuleDefinition module)
    {
        foreach (var type in AllTypes(module))
            foreach (var method in type.Methods)
                if (method.HasBody)
                    yield return (Outermost(type).FullName + "." + method.Name, method);
    }

    private static TypeDefinition Outermost(TypeDefinition type)
    {
        var current = type;
        while (current.DeclaringType is not null) current = current.DeclaringType;
        return current;
    }

    /// <summary>The declaring type of a call, with generic arguments stripped so a call on a constructed
    /// generic type still names the type that declares the method.</summary>
    private static string DeclaringTypeOf(MethodReference method) =>
        method.DeclaringType is null ? "" : method.DeclaringType.GetElementType().FullName;

    /// <summary>Every place the module calls a method whose "Type::Name" starts with <paramref name="seam"/>.</summary>
    private static List<string> CallSitesOf(ModuleDefinition module, string seam)
    {
        var found = new List<string>();
        foreach (var (where, method) in AllBodies(module))
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is MethodReference callee &&
                    (DeclaringTypeOf(callee) + "::" + callee.Name).StartsWith(seam, StringComparison.Ordinal))
                    found.Add(where + " -> " + DeclaringTypeOf(callee) + "::" + callee.Name);
        return found.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every place the module calls one of <paramref name="methodNames"/> on a type in <paramref name="ns"/>.</summary>
    private static List<string> CallSitesInNamespace(ModuleDefinition module, string ns, string[] methodNames)
    {
        var found = new List<string>();
        foreach (var (where, method) in AllBodies(module))
            foreach (var instruction in method.Body.Instructions)
                if (instruction.Operand is MethodReference callee &&
                    DeclaringTypeOf(callee).StartsWith(ns + ".", StringComparison.Ordinal) &&
                    methodNames.Contains(callee.Name, StringComparer.Ordinal))
                    found.Add(where + " -> " + DeclaringTypeOf(callee) + "::" + callee.Name);
        return found.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every place the module loads a string literal containing <paramref name="fragment"/>.</summary>
    private static List<string> LiteralsContaining(ModuleDefinition module, string fragment)
    {
        var found = new List<string>();
        foreach (var (where, method) in AllBodies(module))
            foreach (var instruction in method.Body.Instructions)
                if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string text &&
                    text.Contains(fragment, StringComparison.Ordinal))
                    found.Add(where + " -> \"" + text + "\"");
        return found.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    // ---- the instruments: the scanner is proven on positives before any absence is trusted --------------

    [Fact]
    public void The_harness_assembly_is_beside_the_tests_and_has_code_in_it()
    {
        var bodies = AllBodies(HarnessModule.Value).ToList();
        Assert.True(bodies.Count > 0, "the harness assembly holds no method bodies, so there is nothing to guard.");
    }

    [Fact]
    public void The_call_scanner_finds_the_hosted_brains_own_http_send_in_the_gateway_assembly()
    {
        // THE INSTRUMENT FOR THE HTTP ABSENCE. The hosted brain really does send over HttpClient; if the
        // scanner cannot find it there, "no HTTP in the harness" below is the scanner failing, not the
        // harness passing.
        var sends = CallSitesInNamespace(TheBuiltGatewayAssembly.Module, HttpNamespace, HttpSends);
        Assert.Contains(sends, s => s.StartsWith("CcDirector.Gateway.Wingman.HostedInferenceBrain.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_call_scanner_finds_the_real_prompt_builder_in_the_gateway_assembly()
    {
        // THE INSTRUMENT FOR THE PROMPT ABSENCE: the evaluator really calls BuildPrompt and Read.
        foreach (var seam in TheQuestionsOwnMethods)
        {
            var sites = CallSitesOf(TheBuiltGatewayAssembly.Module, seam);
            Assert.Contains(sites, s => s.StartsWith("CcDirector.Gateway.Rules.RuleEvaluator.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_literal_scanner_finds_the_screen_delimiter_where_the_real_prompt_writes_it()
    {
        // THE INSTRUMENT FOR THE LITERAL ABSENCE: the contract really writes the delimiter.
        var literals = LiteralsContaining(TheBuiltGatewayAssembly.Module, TheScreenDelimiter);
        Assert.Contains(literals, l => l.StartsWith("CcDirector.Gateway.Rules.RuleAgentContract.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_literal_scanner_finds_a_string_the_harness_really_holds()
    {
        var literals = LiteralsContaining(HarnessModule.Value, ALiteralTheHarnessHolds);
        Assert.True(literals.Count > 0, "the literal scanner found no \"" + ALiteralTheHarnessHolds +
                                        "\" in the harness, which really holds it - so the scanner is broken.");
    }

    // ---- the guard --------------------------------------------------------------------------------------

    [Fact]
    public void The_harness_calls_the_real_evaluator()
    {
        var sites = CallSitesOf(HarnessModule.Value, TheEvaluatorCall);
        Assert.True(sites.Count > 0, "the harness never calls " + TheEvaluatorCall + ", so it does not go through the engine.");
    }

    [Fact]
    public void The_harness_constructs_the_real_hosted_brain()
    {
        var sites = CallSitesOf(HarnessModule.Value, TheBrainConstructor);
        Assert.True(sites.Count > 0, "the harness never constructs the hosted brain, so its model call is not the real one.");
    }

    [Fact]
    public void The_harness_never_builds_the_question_or_reads_the_reply_itself()
    {
        foreach (var seam in TheQuestionsOwnMethods)
        {
            var sites = CallSitesOf(HarnessModule.Value, seam);
            Assert.True(sites.Count == 0,
                "the harness calls " + seam + " itself, which makes it a second implementation of the question " +
                "rather than a caller of the engine that asks it:\n  " + string.Join("\n  ", sites));
        }

        var literals = LiteralsContaining(HarnessModule.Value, TheScreenDelimiter);
        Assert.True(literals.Count == 0,
            "the harness holds the screen delimiter the real prompt writes, which is the fingerprint of a " +
            "prompt built here:\n  " + string.Join("\n  ", literals));
    }

    [Fact]
    public void The_harness_makes_no_http_call_of_its_own()
    {
        var sends = CallSitesInNamespace(HarnessModule.Value, HttpNamespace, HttpSends);
        Assert.True(sends.Count == 0,
            "the harness sends over HttpClient itself, so its model call is not only the real hosted brain:\n  " +
            string.Join("\n  ", sends));
    }
}
