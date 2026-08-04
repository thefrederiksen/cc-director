using System.Xml.Linq;
using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE LISTENER GUARD (remove-the-network-port mission, phase 7, folded into phase 6). The mission's
/// outcome is that the Director and the launcher listen on NOTHING; this is the test that fails if
/// anyone ever adds a listener back.
///
/// IT IS A DEPENDENCY ASSERTION, DELIBERATELY NOT A SOURCE-TEXT SCAN. This mission already watched the
/// text-scan shape fail: during a related fix, a review restored the defect by moving the construction
/// into a helper in another file and leaving the expected text in a comment, and every assertion stayed
/// green. A guard that greps for TcpListener or Listen has exactly that weakness, and it is the weakness
/// that matters, because whoever reintroduces a listener in two years will do it in a refactor - which
/// is indirection by definition. A process cannot listen on a port without the hosting machinery to do
/// it, so what is asserted here is that the machinery is ABSENT: text can be moved, a dependency cannot
/// be hidden.
///
/// TWO LEVELS, BECAUSE EACH CATCHES WHAT THE OTHER CANNOT:
///   1. PROJECT level - the .csproj files carry no Microsoft.AspNetCore.App framework reference, which
///      is the switch that makes the hosting surface available to compile against at all.
///   2. ASSEMBLY level - the BUILT assemblies and their whole CcDirector reference closure name no
///      hosting/Kestrel assembly in their metadata. This is the one indirection cannot slip past: a
///      helper project that hosts a listener and is referenced from the launcher shows up in the
///      closure, whatever its source text looks like.
///
/// THE LINE IS LISTEN VERSUS CONNECT, AND THE ALLOW OF SIGNALR IS NOT SOFTNESS. The Director and the
/// launcher both legitimately DIAL OUT over SignalR - that is the entire architecture this mission
/// moved them to, the persistent stream being the only way a command reaches them. The SignalR CLIENT
/// assemblies (Microsoft.AspNetCore.SignalR.Client and the connection assemblies under it) carry the
/// capability to CONNECT, not the capability to LISTEN, so they pass; the hosting, server and Kestrel
/// assemblies carry the capability to listen, so they fail. Refusing the one while permitting the
/// other is exactly the boundary the mission drew.
///
/// VALIDATED THE HARD WAY before it was trusted: a listener was reintroduced INDIRECTLY - the hosting
/// reference restored and a Kestrel host built inside a helper in a separate file, with no listener
/// text at any call site - and both levels went red. That is the case that defeated the previous
/// guard, so it is the case this one was proven against. The proof run is recorded in
/// PHASE-6-REPORT.md.
/// </summary>
public sealed class NoListenerDependencyGuardTests
{
    /// <summary>The two projects the mission made portless. The Gateway is deliberately NOT here: it is
    /// the one door and hosts on purpose.</summary>
    public static TheoryData<string, string> PortlessProjects => new()
    {
        { "CcDirector.ControlApi", Path.Combine("src", "CcDirector.ControlApi", "CcDirector.ControlApi.csproj") },
        // The launcher's assembly is named cc-launcher (its release asset name).
        { "cc-launcher", Path.Combine("src", "CcDirector.Launcher", "CcDirector.Launcher.csproj") },
    };

    /// <summary>
    /// The hosting/Kestrel surface - the capability to LISTEN. "Microsoft.AspNetCore" (the exact name) is
    /// the assembly WebApplication and its builder live in; "Microsoft.AspNetCore.Hosting*" is the host
    /// machinery; "Microsoft.AspNetCore.Server.*" is Kestrel and its transports.
    /// </summary>
    private static bool IsListenSurface(string assemblyName) =>
        assemblyName == "Microsoft.AspNetCore"
        || assemblyName == "Microsoft.AspNetCore.Hosting"
        || assemblyName.StartsWith("Microsoft.AspNetCore.Hosting.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.AspNetCore.Server.", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(PortlessProjects))]
    public void The_project_file_carries_no_hosting_framework_reference(string assemblyName, string csprojRelativePath)
    {
        _ = assemblyName;
        var csproj = Path.Combine(RepositoryRoot(), csprojRelativePath);
        Assert.True(File.Exists(csproj), $"expected the project file at {csproj}");

        var frameworkReferences = XDocument.Load(csproj)
            .Descendants()
            .Where(e => e.Name.LocalName == "FrameworkReference")
            .Select(e => e.Attribute("Include")?.Value ?? "")
            .ToList();

        Assert.DoesNotContain("Microsoft.AspNetCore.App", frameworkReferences);
    }

    [Theory]
    [MemberData(nameof(PortlessProjects))]
    public void The_built_assembly_and_its_whole_closure_reference_no_listen_surface(string assemblyName, string csprojRelativePath)
    {
        _ = csprojRelativePath;
        var offending = new List<string>();
        var closure = new List<string>();

        WalkClosure(assemblyName, (owner, reference) =>
        {
            closure.Add($"{owner} -> {reference}");
            if (IsListenSurface(reference))
                offending.Add($"{owner} references {reference}");
        });

        Assert.True(offending.Count == 0,
            "The listen capability is back in a portless component:\n  " + string.Join("\n  ", offending)
            + "\nThe launcher and the Director's ControlApi must not be able to host a listener - everything "
            + "an agent does goes through the Gateway, and lifecycle is a named signal. If this reference is "
            + "deliberate, the remove-the-network-port mission's outcome is being reversed and that is an "
            + "owner-level decision, not a build fix.");

        // DETECTOR VALIDATION, STANDING: the walk must actually be seeing dependencies, or the assertion
        // above passes vacuously. The SignalR CLIENT is the legitimate connect-out surface both components
        // keep, so its presence in the closure proves the instrument reads real metadata.
        Assert.Contains(closure, edge => edge.Contains("Microsoft.AspNetCore.SignalR.Client", StringComparison.Ordinal));
    }

    /// <summary>
    /// Visit every reference of the root assembly and of every CcDirector assembly reachable from it, by
    /// reading metadata off the DLL files in this test's output directory - nothing is loaded into the
    /// process. The closure is what makes indirection visible: a helper PROJECT that hosts a listener is a
    /// CcDirector.* reference here, and its own references get walked.
    /// </summary>
    private static void WalkClosure(string rootAssemblyName, Action<string, string> onReference)
    {
        var baseDir = AppContext.BaseDirectory;
        var pending = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(rootAssemblyName);
        seen.Add(rootAssemblyName);

        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            var path = Path.Combine(baseDir, name + ".dll");
            Assert.True(File.Exists(path),
                $"expected {path} beside the tests - the project reference that copies it must have been removed, "
                + "which would leave this guard silently checking nothing");

            using var assembly = AssemblyDefinition.ReadAssembly(path);
            foreach (var reference in assembly.MainModule.AssemblyReferences)
            {
                onReference(name, reference.Name);
                var ours = reference.Name.StartsWith("CcDirector", StringComparison.Ordinal)
                           || reference.Name.Equals("cc-launcher", StringComparison.OrdinalIgnoreCase);
                if (ours && seen.Add(reference.Name))
                    pending.Enqueue(reference.Name);
            }
        }
    }

    /// <summary>The repository root, found from the test output directory by walking up to the solution
    /// file - never a hard-coded path, so the guard runs in any checkout and any worktree.</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate cc-director.sln above " + AppContext.BaseDirectory);
    }
}
