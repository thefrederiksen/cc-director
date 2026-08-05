using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE LISTENER GUARD, PART TWO: the BASE CLASS LIBRARY listen surface, asserted where it actually
/// has to be asserted - over the code the portless components can REACH.
///
/// WHY THIS EXISTS. The dependency guard beside this one (<see cref="NoListenerDependencyGuardTests"/>)
/// rests on a premise that is FALSE: that a process cannot listen without ASP.NET hosting machinery.
/// Independent inspection built a project with ordinary SDK references, bound 127.0.0.1, and its
/// reference list contained zero ASP.NET assemblies. So the mission's headline guarantee - nothing CAN
/// listen again - was not enforced by the guard that passed. <c>TcpListener</c>, <c>HttpListener</c> and
/// <c>Socket.Bind</c>/<c>Listen</c> live in the base class library, which every .NET project has.
///
/// WHY IT IS NOT AN ASSEMBLY-LEVEL ASSERTION LIKE ITS SIBLING. It cannot be. <c>CcDirector.Core</c>
/// legitimately CONTAINS listeners, and both portless projects reference Core, so asserting that
/// <c>System.Net.Sockets</c> is absent from the closure would go red on innocent code and be deleted
/// within a week. The capability being PRESENT in a shared library is not the same fact as a portless
/// component USING it, and the guard has to tell those two apart. That is what makes this a call-graph
/// walk rather than a reference list: the question is REACHABILITY from the Director's and the
/// launcher's own code.
///
/// WHAT IT CAN AND CANNOT SEE, stated plainly because an overstated guard is worse than none. It reads
/// instruction operands, so it follows direct calls, constructions, delegate creations and field and
/// type references, and it conservatively treats every method of any CcDirector type reached that way as
/// reachable - which covers dispatch through an interface or a base class. It CANNOT see a call made
/// only by reflection or by name. A listener introduced behind <c>Activator.CreateInstance</c> would
/// pass. Nothing here claims otherwise.
///
/// THE ALLOW LIST IS THE POINT OF THE DESIGN, NOT A WEAKENING OF IT. Two places in Core genuinely bind,
/// both reachable, and both are recorded below with the reason and the limit. A guard with no way to say
/// "this one, for this reason" is a guard that gets switched off the first time it is inconvenient. Each
/// entry must still MATCH something - an allow entry that no longer corresponds to real code fails this
/// test rather than quietly permitting the next thing that takes its name.
/// </summary>
public sealed class NoListenerReachabilityGuardTests
{
    /// <summary>The two components the mission made portless. Same pair the dependency guard covers.</summary>
    public static TheoryData<string> PortlessAssemblies => new() { "CcDirector.ControlApi", "cc-launcher" };

    /// <summary>
    /// The base class library's listen surface - the members that MAKE a process listen, as opposed to
    /// the assemblies that merely contain them. Socket.Connect and the client types are absent on
    /// purpose: this mission's line is listen versus connect, and dialling out is the architecture.
    /// </summary>
    private static bool IsListenMember(MethodReference method)
    {
        var declaringType = method.DeclaringType?.FullName ?? "";
        if (declaringType is "System.Net.HttpListener" or "System.Net.Sockets.TcpListener")
            return true;
        if (declaringType == "System.Net.Sockets.Socket")
            return method.Name is "Bind" or "Listen";
        return false;
    }

    /// <summary>Same surface, seen as a type reference (a field of that type, a cast, a catch clause).</summary>
    private static bool IsListenType(TypeReference type) =>
        type.FullName is "System.Net.HttpListener" or "System.Net.Sockets.TcpListener";

    /// <summary>
    /// The binds a portless component is allowed to reach, each with the reason it is not a listening
    /// service. Keyed by the method that does the binding - not by the type, so a new method on an
    /// allowed type is still a finding.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedBinds = new(StringComparer.Ordinal)
    {
        ["CcDirector.Core.Browsers.AutomationBrowserRegistry::IsPortFreeToBind"] =
            "A transient loopback probe, not a service: it binds 127.0.0.1 on one candidate port to find "
            + "out whether an automation browser's debug port is free, then stops it in the same statement. "
            + "Nothing is ever accepted on it and it holds the port for microseconds. Loopback specifically, "
            + "which is also why it cannot raise the Windows firewall prompt this mission removed.",
    };

    // MEASURED, NOT ASSUMED - and the measurement changed the list. Core's other listener,
    // LoopbackLoginListener (the first-run sign-in credential handback), was written into this allow
    // list on the reasonable assumption that a Director reaches it. The walk says it does NOT: it is not
    // reachable from ControlApi at all. So the entry is gone, and the guard is STRICTER for it - wiring
    // the sign-in listener into the Director later now turns this red rather than finding a waiting
    // permission. An allow list written from expectation would have granted that silently.
    //
    // THE GAP THIS LEAVES, STATED RATHER THAN GLOSSED: what is walked here is the two projects phases 5
    // and 6 emptied. The Director's desktop shell (the Avalonia application that HOSTS ControlApi) is not
    // walked, because its assembly is not built beside these tests, and that shell is where the first-run
    // sign-in listener actually lives. So "no listener" is proven for the components the mission
    // emptied, and the Director process additionally binds loopback during interactive sign-in. Phase 5's
    // runtime scan - zero listening sockets on two live Directors - is the evidence for the steady state;
    // this is the evidence that it cannot come back by refactor. Neither claims the other's ground.

    [Theory]
    [MemberData(nameof(PortlessAssemblies))]
    public void No_base_class_library_listener_is_reachable_from_a_portless_component(string assemblyName)
    {
        var findings = Reachable(assemblyName, out _)
            .Where(f => !AllowedBinds.ContainsKey(f.Key))
            .ToList();

        Assert.True(findings.Count == 0,
            $"{assemblyName} can reach a listener in the base class library:\n  "
            + string.Join("\n  ", findings.Select(f => $"{f.Key} <- reached via {f.Via}"))
            + "\n\nThe portless components must not be able to listen on anything - every agent command "
            + "arrives through the Gateway and lifecycle is a named signal. ASP.NET is not the only way to "
            + "listen: TcpListener, HttpListener and Socket.Bind need no framework reference at all, which "
            + "is exactly the hole this guard exists to close. If a bind here is genuinely not a listening "
            + "service, add it to AllowedBinds WITH ITS REASON - do not widen the surface.");
    }

    /// <summary>
    /// DETECTOR VALIDATION ONE: the walk finds a listener that is really there.
    ///
    /// Asserted over the Director's ControlApi specifically, and NOT over the launcher, because the
    /// launcher genuinely reaches neither allowed bind - a zero there is the right answer, not a
    /// broken instrument. Pinning this to the component that DOES reach one is what makes it a check
    /// on the tool rather than a restatement of the result. It also fails if an allow entry goes
    /// stale, which is deliberate: an entry that matches nothing must not sit there quietly
    /// permitting whatever next takes its name.
    /// </summary>
    [Fact]
    public void The_walk_actually_finds_the_listen_surface_it_permits()
    {
        var found = Reachable("CcDirector.ControlApi", out _).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        Assert.True(found.SetEquals(AllowedBinds.Keys),
            "the reachability walk over CcDirector.ControlApi did not find exactly the listen surface it "
            + "permits, so either it is not reading what it thinks it is reading (and the guard beside it "
            + "passes vacuously) or an allow entry has gone stale.\n  found:   "
            + (found.Count == 0 ? "(nothing)" : string.Join(", ", found.Order()))
            + "\n  allowed: " + string.Join(", ", AllowedBinds.Keys.Order()));
    }

    /// <summary>
    /// DETECTOR VALIDATION TWO, and it is the one that makes the LAUNCHER's clean result mean anything.
    /// A walk that never left the root assembly would report zero listeners for a component whose
    /// listener sits one project away, and would look exactly like a pass. So each walk must be shown
    /// to have crossed into the shared library where the listen surface actually lives.
    ///
    /// It names a TYPE each component provably uses rather than counting types visited. A count is a
    /// magic number that means nothing when it changes: the first version of this asserted "more than
    /// fifty" and went red at forty-five on a walk that had crossed the boundary perfectly well.
    /// </summary>
    [Theory]
    [InlineData("cc-launcher", "CcDirector.Core.Lifecycle.LifecycleSignal")]
    [InlineData("CcDirector.ControlApi", "CcDirector.Core.Sessions.SessionManager")]
    public void The_walk_crosses_out_of_the_root_assembly_into_the_shared_library(string assemblyName, string mustReach)
    {
        _ = Reachable(assemblyName, out var visited);

        Assert.True(visited.Contains(mustReach),
            $"the walk over {assemblyName} never reached {mustReach}, a type it demonstrably uses, so it "
            + "did not follow calls out of the root assembly. A walk that stops at the assembly boundary "
            + "cannot see a listener in a shared library - which is where ours are - so it would report a "
            + $"clean result for the wrong reason. Types visited: {visited.Count}.");
    }

    private readonly record struct Finding(string Key, string Via);

    /// <summary>
    /// Every listen-surface use reachable from <paramref name="rootAssembly"/>'s own code, following
    /// calls into CcDirector assemblies. Metadata only - nothing is loaded into this process.
    /// </summary>
    private static List<Finding> Reachable(string rootAssembly, out HashSet<string> visitedTypeNames)
    {
        var baseDir = AppContext.BaseDirectory;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(baseDir);
        var readerParameters = new ReaderParameters { AssemblyResolver = resolver };

        var assemblies = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);

        AssemblyDefinition? Load(string name)
        {
            if (assemblies.TryGetValue(name, out var cached)) return cached;
            var path = Path.Combine(baseDir, name + ".dll");
            if (!File.Exists(path)) return null;
            var assembly = AssemblyDefinition.ReadAssembly(path, readerParameters);
            assemblies[name] = assembly;
            return assembly;
        }

        var root = Load(rootAssembly);
        Assert.True(root is not null,
            $"expected {rootAssembly}.dll beside the tests - if the project reference that copies it was "
            + "removed, this guard would silently be checking nothing");

        static bool Ours(string? assemblyName) =>
            assemblyName is not null
            && (assemblyName.StartsWith("CcDirector", StringComparison.Ordinal)
                || assemblyName.Equals("cc-launcher", StringComparison.OrdinalIgnoreCase));

        var findings = new List<Finding>();
        var visitedMethods = new HashSet<string>(StringComparer.Ordinal);
        var visitedTypes = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<MethodDefinition>();

        void EnqueueType(TypeDefinition? type)
        {
            if (type is null || !visitedTypes.Add(type.FullName)) return;
            // EVERY method of a reached type, not only the one that was called. This is the conservative
            // half: dispatch through an interface or a base class resolves to a declaration this walk
            // cannot follow, so reaching the type at all is treated as reaching its behaviour.
            foreach (var method in type.Methods)
                if (method.HasBody && visitedMethods.Add(method.FullName))
                    pending.Enqueue(method);
            foreach (var nested in type.NestedTypes)
                EnqueueType(nested);
        }

        foreach (var type in root!.MainModule.Types)
            EnqueueType(type);

        while (pending.Count > 0)
        {
            var method = pending.Dequeue();
            var owner = $"{method.DeclaringType.FullName}::{method.Name}";

            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference called:
                        if (IsListenMember(called))
                            findings.Add(new Finding(owner, $"{called.DeclaringType.FullName}::{called.Name}"));
                        else if (Ours(called.DeclaringType?.Scope?.Name?.Replace(".dll", "", StringComparison.OrdinalIgnoreCase)))
                            EnqueueType(Resolve(called.DeclaringType, Load));
                        break;

                    case TypeReference referenced:
                        if (IsListenType(referenced))
                            findings.Add(new Finding(owner, referenced.FullName));
                        else if (Ours(referenced.Scope?.Name?.Replace(".dll", "", StringComparison.OrdinalIgnoreCase)))
                            EnqueueType(Resolve(referenced, Load));
                        break;

                    case FieldReference field:
                        if (IsListenType(field.FieldType))
                            findings.Add(new Finding(owner, field.FieldType.FullName));
                        else if (Ours(field.DeclaringType?.Scope?.Name?.Replace(".dll", "", StringComparison.OrdinalIgnoreCase)))
                            EnqueueType(Resolve(field.DeclaringType, Load));
                        break;
                }
            }
        }

        visitedTypeNames = visitedTypes;
        return findings;
    }

    /// <summary>A type reference resolved to its definition in whichever of our assemblies declares it,
    /// or null when it is not ours to walk.</summary>
    private static TypeDefinition? Resolve(TypeReference? reference, Func<string, AssemblyDefinition?> load)
    {
        if (reference is null) return null;
        var scope = reference.Scope?.Name?.Replace(".dll", "", StringComparison.OrdinalIgnoreCase);
        if (scope is null) return null;
        var assembly = load(scope);
        return assembly?.MainModule.GetType(reference.FullName);
    }
}
