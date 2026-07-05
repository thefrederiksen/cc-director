using System.Runtime.Versioning;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the shared base-Python runtime probe (issue #995): it must report a hollow or absent Python as
/// unhealthy (so the Director triggers a self-repair) and a real, runnable interpreter as healthy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PythonRuntimeProbeTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public PythonRuntimeProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-pyprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void CanImportStdlib_MissingFile_False()
    {
        Assert.False(PythonRuntimeProbe.CanImportStdlib(Path.Combine(_dir, "nope", "python.exe")));
    }

    [Fact]
    public void CanImportStdlib_NonExecutableFile_False()
    {
        // A "python.exe" that is not a valid executable (the shape of a hollow/corrupt install) cannot start,
        // so the probe reports the runtime as dead rather than throwing.
        var fake = Path.Combine(_dir, "python.exe");
        File.WriteAllText(fake, "not a real interpreter");
        Assert.False(PythonRuntimeProbe.CanImportStdlib(fake));
    }

    [Fact]
    public void IsBasePythonHealthy_NoPythonDir_False()
    {
        // No base Python installed at all -> unhealthy (the Director should offer/trigger a repair).
        Assert.False(PythonRuntimeProbe.IsBasePythonHealthy(_layout));
    }

    [Fact]
    public void CanImportStdlib_RealSystemPython_True()
    {
        // If a real Python is available on this machine, the probe must recognize it as healthy. Skipped when
        // no interpreter is on PATH (the assertion would have nothing to prove).
        var python = ResolveOnPath("python.exe") ?? ResolveOnPath("python3.exe");
        if (python is null) return; // no system Python to validate against; nothing to assert
        Assert.True(PythonRuntimeProbe.CanImportStdlib(python));
    }

    private static string? ResolveOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry; skip */ }
        }
        return null;
    }
}
