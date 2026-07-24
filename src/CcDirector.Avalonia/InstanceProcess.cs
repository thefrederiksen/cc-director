using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Spawns another named Director instance as a clean, independent process:
/// <c>cc-director.exe --instance &lt;slug&gt;</c>.
///
/// The child must resolve the TRUE machine-wide shared root by itself, so we explicitly
/// drop <c>CC_DIRECTOR_ROOT</c> from the child's environment - otherwise a named-instance
/// parent (which has that override set to its own home) would leak it and the child would
/// mistake its parent's home for the shared root.
/// </summary>
internal static class InstanceProcess
{
    public static void Launch(string slug)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the current executable path.");
        FileLog.Write($"[InstanceProcess] Launch: slug={slug}, exe={exe}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--instance");
        psi.ArgumentList.Add(slug);
        psi.Environment.Remove("CC_DIRECTOR_ROOT");

        Process.Start(psi);
    }
}
