using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// EXACTLY ONE THING IN THIS FEATURE CAN TYPE, AND THIS IS WHAT PROVES IT. Phase 1 typed nothing at all;
/// phase 2 types, which is the whole point of it - so the guard is not deleted, it is TIGHTENED. The set of
/// types in the rules namespace that reach the seam that types into a session must be exactly one, and it
/// must be the production environment wiring. The evaluator - which is where the dry-run decision is made -
/// must not be able to type at all, which is what makes "dry run types nothing" a property of the structure
/// rather than a branch somebody has to keep remembering.
///
/// IT IS A REFERENCE ASSERTION IN THE BUILT ASSEMBLY, DELIBERATELY NOT A SOURCE-TEXT SCAN, and the
/// distinction is the point. A grep for "prompt" over the rules directory would pass just as happily if
/// the rules directory were EMPTY, or if the call sat one helper away in another file. Reading the
/// compiled metadata cannot be dodged by moving text, and - the part that matters most - the scanner is
/// PROVEN TO WORK on a known-positive first: it is pointed at code that really does type into sessions and
/// required to find the seam there. If that instrument check ever comes back clean, the scanner is broken
/// and every "no reference found" below it means nothing.
///
/// IT FOLLOWS CALLS NOW, WHICH IT DID NOT. The independent inspection of landing A found the scanner read
/// each method's IMMEDIATE operands only, so a rules type that reached the send through a helper in
/// another namespace left it green. It was run against exactly that shape - a probe committed on purpose
/// and left in the history - and it passed. It now walks the call graph, so a route through any number of
/// helpers inside this assembly is found.
///
/// WHAT IT COVERS, EXACTLY, because a guard described more broadly than it holds is worse than none:
///
///  - Every type of the FEATURE: the rules namespace, and the rule and firing entities, which live in the
///    data namespace and were outside the old filter entirely.
///  - STATIC call edges inside the Gateway assembly, followed transitively. A call to a method in another
///    assembly is not followed into it.
///  - It does NOT follow VIRTUAL DISPATCH. The evaluator calls
///    <c>IRuleEnvironment.TypeIntoSessionAsync</c>, and reaching the send through that seam is the design
///    rather than a leak - it is what the dry-run branch sits in front of. So the assertion about the
///    evaluator below is that it cannot reach the send EXCEPT through its environment, and that is what it
///    says.
/// </summary>
public sealed class RulesTypeNothingGuardTests
{
    /// <summary>THE SEAM THAT TYPES: the prompt verb, which puts text into a session's composer and presses
    /// Enter. Naming the METHOD rather than its class matters - the same class also READS the screen, and a
    /// guard that could not tell a read from a keystroke would refuse the read as well.</summary>
    private const string TypingSeam = "CcDirector.Gateway.Api.SessionVerbClient::PostPromptAsync";

    /// <summary>The lower-level router the prompt verb goes through. Nothing in this feature reaches it
    /// directly - the feature types through the verb client like every other caller.</summary>
    private const string CommandRouter = "CcDirector.Gateway.Api.DirectorCommandRouter";

    /// <summary>The namespace this feature is built in.</summary>
    private const string RulesNamespace = "CcDirector.Gateway.Rules";

    /// <summary>Code that really does type into sessions, so the scanner can be proven against it: the
    /// session supervisor's production wiring, which sends "continue" into a parked session.</summary>
    private const string KnownTypist = "CcDirector.Gateway.Supervision.GatewaySupervisorEnvironment";

    /// <summary>The ONE type in this feature that is allowed to type. Named, so a second one is a failing
    /// test rather than a thing nobody notices.</summary>
    private const string TheOnlyTypist = "CcDirector.Gateway.Rules.GatewayRuleEnvironment";

    /// <summary>Where the dry-run decision is made. It must be structurally incapable of typing.</summary>
    private const string TheEvaluator = "CcDirector.Gateway.Rules.RuleEvaluator";

    /// <summary>
    /// THE ASSEMBLY AND ITS CALL GRAPH, READ ONCE FOR THE WHOLE TEST PROCESS.
    ///
    /// This is not a micro-optimisation, it is the difference between the gate running and the gate being
    /// killed. Building the graph means resolving every call site in the Gateway assembly to a definition,
    /// and doing that once per test pushed this project past the two-minute ceiling the local gate enforces
    /// - the suite was STOPPED, which is not a failure and is not a pass either. Built once, shared, and
    /// held for the life of the process, because a module that is disposed takes the type handles in the
    /// index with it.
    /// </summary>
    private static readonly Lazy<ModuleDefinition> Module = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CcDirector.Gateway.dll");
        Assert.True(File.Exists(path), "the Gateway assembly is not beside the tests at " + path);
        return ModuleDefinition.ReadModule(path);
    }, isThreadSafe: true);

    private static ModuleDefinition GatewayModule() => Module.Value;

    /// <summary>Who calls what, plus where each method lives. Seam-independent, so one build serves every
    /// question any test asks.</summary>
    private sealed record CallIndex(
        Dictionary<string, TypeDefinition> Owner,
        Dictionary<string, string> Name,
        Dictionary<string, List<string>> Callers);

    private static readonly Lazy<CallIndex> Graph = new(BuildCallIndex, isThreadSafe: true);

    private static CallIndex BuildCallIndex()
    {
        var module = GatewayModule();
        var owner = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        var name = new Dictionary<string, string>(StringComparer.Ordinal);
        var callers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var type in AllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                owner[method.FullName] = type;
                name[method.FullName] = method.Name;
                if (!method.HasBody) continue;

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference callee) continue;
                    var key = DefinitionKey(callee);
                    if (key is null) continue;
                    if (!callers.TryGetValue(key, out var list)) callers[key] = list = new List<string>();
                    list.Add(method.FullName);
                }
            }
        }
        return new CallIndex(owner, name, callers);
    }

    /// <summary>Every method whose OWN body references <paramref name="seam"/>, among the types this
    /// filter selects. One hop only - this is the scanner as it was, kept because the transitive one is
    /// measured against it.</summary>
    private static List<string> MethodsReaching(ModuleDefinition module, string seam, Func<TypeDefinition, bool> include)
    {
        var found = new List<string>();
        foreach (var type in AllTypes(module))
        {
            if (!include(type)) continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                if (Mentions(method, seam)) found.Add(Outermost(type).FullName + "." + method.Name);
            }
        }
        return found;
    }

    /// <summary>Whether a method's own body names the seam.</summary>
    private static bool Mentions(MethodDefinition method, string seam)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            var reference = instruction.Operand switch
            {
                MethodReference m => (m.DeclaringType?.FullName ?? "") + "::" + m.Name,
                FieldReference f => (f.DeclaringType?.FullName ?? "") + "::" + f.Name,
                TypeReference t => t.FullName,
                _ => null,
            };
            if (reference is not null && reference.StartsWith(seam, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Every method that reaches <paramref name="seam"/> DIRECTLY OR THROUGH ANY CHAIN OF CALLS inside
    /// this assembly, among the types the filter selects. This is the answer the guard is actually about:
    /// a rules type that sends through a helper in another namespace is typing, and a scanner that only
    /// read immediate operands called that clean.
    ///
    /// Edges are static call sites, resolved to a method DEFINITION in this module; a call into another
    /// assembly is an edge to nothing, and a virtual call is an edge to the method that was named, not to
    /// its implementations. Both limits are stated on the class, and neither is silent.
    /// </summary>
    private static List<string> MethodsReachingThroughCalls(
        ModuleDefinition module, string seam, Func<TypeDefinition, bool> include)
    {
        var graph = Graph.Value;
        var reached = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in AllTypes(module))
            foreach (var method in type.Methods)
                if (method.HasBody && Mentions(method, seam))
                    reached.Add(method.FullName);

        var queue = new Queue<string>(reached);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!graph.Callers.TryGetValue(current, out var itsCallers)) continue;
            foreach (var caller in itsCallers)
                if (reached.Add(caller)) queue.Enqueue(caller);
        }

        return reached
            .Where(m => graph.Owner.TryGetValue(m, out var t) && include(t))
            .Select(m => Outermost(graph.Owner[m]).FullName + "." + graph.Name[m])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The called method's definition key, or null when it cannot be resolved in this
    /// assembly.</summary>
    private static string? DefinitionKey(MethodReference reference)
    {
        try
        {
            var definition = reference.Resolve();
            return definition?.FullName;
        }
        catch
        {
            // A reference into an assembly that is not beside us is not an edge we can follow. It is not
            // an error, and it is not a clean result either - the class comment says so.
            return null;
        }
    }

    /// <summary>The distinct types among those methods.</summary>
    private static List<string> TypesOf(IEnumerable<string> methods) => methods
        .Select(m => m[..m.LastIndexOf('.')])
        .Distinct(StringComparer.Ordinal)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// The type a nested one belongs to, walking all the way out. An ASYNC METHOD'S BODY DOES NOT LIVE IN
    /// THE METHOD: the compiler moves it into a generated nested state machine, and in the metadata that
    /// nested type carries an EMPTY namespace and a name of its own. A guard that did not walk out to the
    /// declaring type would therefore find nothing for every async method in the namespace it is guarding -
    /// and would report that as a clean result. This one cost a red before it was noticed.
    /// </summary>
    private static TypeDefinition Outermost(TypeDefinition type)
    {
        var current = type;
        while (current.DeclaringType is not null) current = current.DeclaringType;
        return current;
    }

    /// <summary>The namespace a type really belongs to - its own, or its outermost declaring type's.</summary>
    private static string NamespaceOf(TypeDefinition type) => Outermost(type).Namespace ?? "";

    /// <summary>The namespace the feature's stored rows live in.</summary>
    private const string EntitiesNamespace = "CcDirector.Gateway.Data.Entities";

    /// <summary>
    /// EVERY TYPE OF THIS FEATURE, which is more than one namespace. The guard used to select the rules
    /// namespace alone, and the landing that introduced it also added the rule and firing entities in the
    /// data namespace - so the feature's own stored rows were outside the thing guarding the feature.
    /// The entities are picked out by the names the feature gives them rather than by a list kept here,
    /// so a third one is covered on the day it is written.
    /// </summary>
    private static bool IsFeatureType(TypeDefinition type)
    {
        if (NamespaceOf(type).StartsWith(RulesNamespace, StringComparison.Ordinal)) return true;
        if (!string.Equals(NamespaceOf(type), EntitiesNamespace, StringComparison.Ordinal)) return false;
        var simple = Outermost(type).Name;
        return simple.StartsWith("SessionRule", StringComparison.Ordinal)
            || simple.StartsWith("RulePrimitive", StringComparison.Ordinal);
    }

    /// <summary>Every type in the module, nested ones included - a compiler-generated closure is still a
    /// type that could hold the call.</summary>
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

    [Fact]
    public void The_scanner_finds_the_typing_seam_where_it_really_is()
    {
        // THE INSTRUMENT. If this comes back empty the scanner is broken, and every guard below it would
        // certify a run that never looked at anything.
        var module = GatewayModule();
        var typists = MethodsReaching(module, TypingSeam,
            t => Outermost(t).FullName.StartsWith(KnownTypist, StringComparison.Ordinal));

        Assert.True(typists.Count > 0,
            "the scanner found no reference to " + TypingSeam + " inside " + KnownTypist +
            ", which really does type into sessions - so the scanner is broken, not the code.");
    }

    [Fact]
    public void The_rules_namespace_exists_and_has_types_in_it()
    {
        // The second instrument: a guard over an empty namespace passes and proves nothing.
        var module = GatewayModule();
        var ruleTypes = AllTypes(module)
            .Where(t => NamespaceOf(t).StartsWith(RulesNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(ruleTypes.Count > 0, "no types were found in " + RulesNamespace);
    }

    [Fact]
    public void The_feature_includes_its_stored_rows_and_not_the_whole_data_namespace()
    {
        // The third instrument. The guard's scope grew to cover the rule and firing entities, and a scope
        // that quietly selected nothing - or everything - would make every assertion below it meaningless
        // in opposite directions.
        var module = GatewayModule();
        var feature = AllTypes(module).Where(IsFeatureType).Select(t => Outermost(t).FullName).Distinct().ToList();

        Assert.Contains("CcDirector.Gateway.Data.Entities.SessionRuleEntity", feature);
        Assert.Contains("CcDirector.Gateway.Data.Entities.SessionRuleFiringEntity", feature);
        Assert.Contains("CcDirector.Gateway.Rules.RuleEvaluator", feature);
        Assert.DoesNotContain("CcDirector.Gateway.Data.Entities.CronJobEntity", feature);
    }

    /// <summary>
    /// A seam with CHAINS behind it, used only to prove the traversal traverses. It is deliberately NOT
    /// the typing seam, and the reason is worth writing down: with the probe removed, nothing in this
    /// assembly reaches the prompt verb except by calling it directly, so on that seam a call-graph walk
    /// and a one-hop read give the same four methods - which is the truth about the code, and useless as a
    /// test of the walk. The log write is called from all over the Gateway by methods that are themselves
    /// called, so it exercises the edges. The traversal is one algorithm, so proving it on this seam
    /// proves it for the seam the guard is about.
    /// </summary>
    private const string SeamWithChainsBehindIt = "CcDirector.Core.Utilities.FileLog::Write";

    [Fact]
    public void Following_calls_finds_more_than_reading_one_method_at_a_time()
    {
        // THE INSTRUMENT FOR THE TRAVERSAL ITSELF. A call-graph walk that followed no edges would answer
        // exactly what the old one-hop scanner answered, and every assertion built on it would be the old
        // guard wearing a new name. So it has to find strictly more.
        var module = GatewayModule();
        var oneHop = MethodsReaching(module, SeamWithChainsBehindIt, _ => true)
            .Distinct(StringComparer.Ordinal).ToList();
        var throughCalls = MethodsReachingThroughCalls(module, SeamWithChainsBehindIt, _ => true);

        Assert.True(oneHop.Count > 0, "the one-hop scanner found nothing, so the comparison is meaningless.");
        Assert.True(throughCalls.Count > oneHop.Count,
            "following calls found " + throughCalls.Count + " methods reaching " + SeamWithChainsBehindIt +
            " and reading one method at a time found " + oneHop.Count +
            ". They are the same, so the traversal is not traversing.");
    }

    [Fact]
    public void Nothing_reaches_the_typing_seam_except_by_calling_it_and_that_is_recorded_not_assumed()
    {
        // The fact that made the instrument above need a different seam, asserted rather than left as a
        // remark: today every method that reaches the prompt verb calls it itself. The day that stops
        // being true this test fails, and whoever reads it will find out from here rather than from a
        // guard quietly measuring a longer list.
        var module = GatewayModule();
        var oneHop = MethodsReaching(module, TypingSeam, _ => true).Distinct(StringComparer.Ordinal).Count();
        var throughCalls = MethodsReachingThroughCalls(module, TypingSeam, _ => true).Count;

        Assert.True(oneHop > 0, "nothing at all reaches " + TypingSeam + ", so the scanner is broken.");
        Assert.Equal(oneHop, throughCalls);
    }

    [Fact]
    public void Exactly_one_type_in_the_whole_feature_can_type_and_it_is_the_named_one()
    {
        var module = GatewayModule();
        var typists = TypesOf(MethodsReachingThroughCalls(module, TypingSeam, IsFeatureType));

        // A PRESENCE first: the one thing that is supposed to be able to type must actually be there. An
        // empty list would otherwise pass the "nothing else can type" half while proving that the feature
        // cannot type at all.
        Assert.Contains(TheOnlyTypist, typists);
        Assert.Equal(new[] { TheOnlyTypist }, typists);
    }

    [Fact]
    public void The_evaluator_reaches_the_send_only_through_its_environment_seam()
    {
        var module = GatewayModule();
        var offenders = MethodsReachingThroughCalls(module, TypingSeam,
            t => Outermost(t).FullName.StartsWith(TheEvaluator, StringComparison.Ordinal));

        // Said exactly: the evaluator DOES reach the send, through IRuleEnvironment, and that is the
        // design - the dry-run branch sits in front of it. What must not exist is a static route from the
        // evaluator to the prompt verb that goes round that seam, because the branch would then be
        // something somebody has to remember rather than something the shape of the code enforces.
        Assert.True(offenders.Count == 0,
            "the evaluator decides whether a rule is in dry run, so it must reach the send only through its " +
            "environment seam - but these reach " + TypingSeam + " without it: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Nothing_in_the_rules_namespace_reaches_the_command_router_directly()
    {
        var module = GatewayModule();

        // The instrument for THIS seam, so the assertion below cannot pass on a scanner that finds nothing:
        // the endpoint layer really does reach the router.
        var known = MethodsReaching(module, CommandRouter,
            t => Outermost(t).FullName.StartsWith("CcDirector.Gateway.Api.GatewayEndpoints", StringComparison.Ordinal));
        Assert.True(known.Count > 0,
            "the scanner found no reference to " + CommandRouter + " in the endpoint layer, which really " +
            "does use it - so the scanner is broken, not the code.");

        var offenders = MethodsReachingThroughCalls(module, CommandRouter, IsFeatureType);

        Assert.True(offenders.Count == 0,
            "this feature types through the prompt verb like every other caller, but these reach " +
            CommandRouter + ": " + string.Join(", ", offenders));
    }
}
