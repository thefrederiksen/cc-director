using System.Diagnostics;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE CROSS-PROCESS FILE LOCK, PROVEN ACROSS TWO REAL PROCESSES.
///
/// WHY THIS FILE EXISTS. <see cref="TenantGlossaryWriterRaceTests"/> proves the writer is atomic, but every
/// one of its races runs inside ONE process, where the static per-tenant monitor already serialises
/// everything. So those tests are BLIND to the file lock: with <c>AcquireFileLock</c> deleted outright, all
/// eight of them still pass - measured, not assumed. A guard whose removal changes no test is a guard
/// nothing is holding, and the claim that this Gateway is safe against two containers on one shared file
/// share rested entirely on reading the code.
///
/// The file lock exists for OVERLAPPING GATEWAY PROCESSES on one file share - a state a hosted deploy has
/// really been in - so it is proven where it operates: this test starts a SECOND operating-system process
/// that writes the same tenant's glossary at the same time, and asserts that every term that survives has
/// its provenance entry. The two processes share nothing but the directory, so the in-process monitor
/// cannot be what makes it pass.
///
/// HOW THE SECOND PROCESS IS RUN. It is this same test assembly, re-entered by <c>dotnet test</c> with a
/// filter selecting <see cref="CrossProcessGlossaryWriterHelper"/> - the repository has no spare console
/// project to borrow, and adding one to carry a test would be a heavier change than the test. The helper is
/// inert unless its environment variable is present, so a normal suite run never executes the writing path.
///
/// WHAT THIS STILL DOES NOT PROVE, stated plainly rather than left implied: it proves the lock serialises
/// two processes on THIS machine's local file system. It does NOT prove that the operating system's file
/// locking is honoured by the hosted deployment's network file share, which no test in this repository can
/// reach. If that guarantee matters it needs a check against the real share, not a unit test.
/// </summary>
public sealed class CrossProcessGlossaryLockTests : IDisposable
{
    /// <summary>Set for the child process only. Its presence is what wakes the helper up.</summary>
    internal const string ChildRootVar = "CC_TEST_GLOSSARY_CHILD_ROOT";
    internal const string ChildTenantVar = "CC_TEST_GLOSSARY_CHILD_TENANT";
    internal const string ChildReadyVar = "CC_TEST_GLOSSARY_CHILD_READY";
    internal const string ChildGoVar = "CC_TEST_GLOSSARY_CHILD_GO";

    /// <summary>Writes per side. Enough overlap that the unlocked version cannot get through by luck.</summary>
    internal const int WritesPerSide = 40;

    private readonly string _root;
    private readonly string? _priorRoot;

    public CrossProcessGlossaryLockTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "cc-glossary-xproc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    [Trait("Category", "CrossProcess")]
    public void Two_processes_writing_one_tenants_glossary_lose_no_provenance()
    {
        var tenant = $"xproc-{Guid.NewGuid():N}";
        var readyPath = Path.Combine(_root, "child-ready");
        var goPath = Path.Combine(_root, "go");

        using var child = StartChild(tenant, readyPath, goPath);
        Assert.True(child is not null, "could not start the second process");

        // Wait for the child's test host to boot and reach the helper. This is the slow part - a test host
        // start, not the race - so it gets a generous window.
        var bootDeadline = DateTime.UtcNow.AddMinutes(3);
        while (!File.Exists(readyPath) && DateTime.UtcNow < bootDeadline)
        {
            if (child!.HasExited)
                Assert.Fail($"the second process exited before signalling ready (exit {child.ExitCode}):\n{ReadOutput(child)}");
            Thread.Sleep(100);
        }
        Assert.True(File.Exists(readyPath), "the second process never signalled ready; cannot race what did not start");

        // Release both sides together.
        File.WriteAllText(goPath, "go");
        WriteMany(new TenantId(tenant), "parent", WritesPerSide);

        Assert.True(child!.WaitForExit(120_000), "the second process did not finish in time");

        var glossary = TenantGlossary.Load(new TenantId(tenant));
        var trail = GlossaryAdditionLog.Read(new TenantId(tenant));

        // The child must actually have written, or this proves nothing at all - a silently-failed child
        // would leave the parent racing itself and passing.
        Assert.True(
            trail.Any(e => e.SessionId == "child"),
            $"the second process wrote nothing, so no cross-process race happened. Child output:\n{ReadOutput(child)}");
        Assert.True(trail.Any(e => e.SessionId == "parent"), "the parent wrote nothing");

        // THE ASSERTION. Every word that survives is attributable. Unlocked, two processes tear the shared
        // staging file and lose trail lines, so terms arrive with no entry naming who added them.
        foreach (var term in glossary.Vocabulary)
            Assert.True(trail.Any(e => e.Term == term),
                $"'{term}' is in the glossary with NO trail entry after a two-process race - it cannot be " +
                $"traced or swept. Glossary has {glossary.Vocabulary.Count} terms, trail has {trail.Count} entries.");

        // And nothing was lost outright: both sides' full batches are present.
        Assert.Equal(WritesPerSide * 2, glossary.Vocabulary.Count);
    }

    /// <summary>Write <paramref name="count"/> terms as one session, the way the endpoint composes an add.</summary>
    internal static void WriteMany(TenantId tenant, string session, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var term = $"{session}-term-{i}";
            TenantGlossaryWriter.MutateAndRecord(
                tenant,
                current =>
                {
                    var vocab = current.Vocabulary.ToList();
                    var added = new List<string>();
                    if (!vocab.Contains(term))
                    {
                        vocab.Add(term);
                        added.Add(term);
                    }
                    return (new DictationDictionary(vocab, current.CommonMistranscriptions, current.Profiles),
                            (IReadOnlyList<string>)added);
                },
                session,
                "director-xproc",
                new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc));
        }
    }

    private Process? StartChild(string tenant, string readyPath, string goPath)
    {
        var assembly = typeof(CrossProcessGlossaryLockTests).Assembly.Location;
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
        };
        info.ArgumentList.Add("test");
        info.ArgumentList.Add(assembly);
        info.ArgumentList.Add("--no-build");
        info.ArgumentList.Add("--nologo");
        info.ArgumentList.Add("--filter");
        info.ArgumentList.Add("FullyQualifiedName~CrossProcessGlossaryWriterHelper");

        // The child needs the same data root, and the coordination paths. CC_DIRECTOR_ROOT is what makes it
        // resolve the SAME tenant directory this process is using.
        info.Environment["CC_DIRECTOR_ROOT"] = _root;
        info.Environment[ChildRootVar] = _root;
        info.Environment[ChildTenantVar] = tenant;
        info.Environment[ChildReadyVar] = readyPath;
        info.Environment[ChildGoVar] = goPath;

        var process = Process.Start(info);
        if (process is null) return null;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.Add(e.Data); };
        return process;
    }

    private readonly List<string> _output = new();

    private string ReadOutput(Process? process)
    {
        lock (_output)
            return _output.Count == 0 ? "(no output captured)" : string.Join("\n", _output.TakeLast(40));
    }
}

/// <summary>
/// The writing half of <see cref="CrossProcessGlossaryLockTests"/>, run in a SECOND process.
///
/// It is a test only because re-entering this assembly with <c>dotnet test</c> is the cheapest way to get a
/// second process that already has the Gateway loaded. It does NOTHING unless the coordinating environment
/// variables are set, so an ordinary suite run executes the first line and returns - it never writes, never
/// touches a glossary, and cannot interfere with anything.
/// </summary>
public sealed class CrossProcessGlossaryWriterHelper
{
    [Fact]
    [Trait("Category", "CrossProcess")]
    public void WriteAsTheSecondProcess()
    {
        var tenant = Environment.GetEnvironmentVariable(CrossProcessGlossaryLockTests.ChildTenantVar);
        var readyPath = Environment.GetEnvironmentVariable(CrossProcessGlossaryLockTests.ChildReadyVar);
        var goPath = Environment.GetEnvironmentVariable(CrossProcessGlossaryLockTests.ChildGoVar);

        // Not the child. This is the ordinary suite running, and there is nothing to do.
        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(readyPath) || string.IsNullOrEmpty(goPath))
            return;

        File.WriteAllText(readyPath, "ready");

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!File.Exists(goPath) && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.True(File.Exists(goPath), "the parent never released the race");

        CrossProcessGlossaryLockTests.WriteMany(
            new TenantId(tenant), "child", CrossProcessGlossaryLockTests.WritesPerSide);
    }
}
