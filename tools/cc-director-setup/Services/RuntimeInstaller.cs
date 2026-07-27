using System.Diagnostics;

namespace CcDirectorSetup.Services;

/// <summary>The outcome of an automatic runtime install attempt.</summary>
/// <param name="Success">Did it install.</param>
/// <param name="Message">What the screen shows - never a raw error number, never a tool name.</param>
/// <param name="Failure">Why it did not, for the row's own status. Null on success.</param>
public sealed record RuntimeInstallResult(bool Success, string Message, RuntimeInstallFailure? Failure = null);

/// <summary>
/// Installs a prerequisite runtime via winget. Used by the Prerequisites step to fetch the
/// .NET 10 ASP.NET Core runtime when it is missing. If winget is not available or the install
/// fails, the result is an explicit failure (no silent fallback) so the UI can fall back to the
/// manual download link with a clear message.
/// </summary>
public static class RuntimeInstaller
{
    /// <param name="displayName">The row's name, so a failure names the tool the user clicked on
    /// rather than the one this class was originally written for.</param>
    public static async Task<RuntimeInstallResult> InstallAsync(string wingetId, string displayName = "this prerequisite")
    {
        SetupLog.Write($"[RuntimeInstaller] InstallAsync: wingetId={wingetId}");

        var winget = ResolveWinget();
        if (winget == null)
        {
            SetupLog.Write("[RuntimeInstaller] winget not available");
            return Fail(displayName, RuntimeInstallFailure.Other);
        }

        var args =
            $"install --id {wingetId} --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

        var (exitCode, output) = await Task.Run(() => RunWinget(winget, args));
        if (exitCode == 0)
        {
            SetupLog.Write("[RuntimeInstaller] install succeeded");
            return new RuntimeInstallResult(true, "Installed. Re-checking...");
        }

        var failure = RuntimeInstallDiagnosis.Classify(exitCode);

        // The raw code goes HERE, in the log, where it diagnoses instead of confusing. In decimal
        // and hex, because the package manager documents its codes in hex and reports them signed.
        SetupLog.Write(
            $"[RuntimeInstaller] install failed: exit={exitCode} (0x{exitCode:X8}), classified={failure}; {Trim(output)}");

        return Fail(displayName, failure);
    }

    private static RuntimeInstallResult Fail(string displayName, RuntimeInstallFailure failure) =>
        new(false, RuntimeInstallDiagnosis.Message(displayName, failure, SetupLog.Path), failure);

    /// <summary>
    /// winget is not always on the process PATH (it lives under WindowsApps). Prefer the PATH
    /// entry, then the per-user WindowsApps app-execution-alias location.
    /// </summary>
    private static string? ResolveWinget()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias))
            return alias;

        // Fall back to bare "winget" so the OS resolves it from PATH if present.
        var psi = new ProcessStartInfo
        {
            FileName = "where",
            Arguments = "winget",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var probe = Process.Start(psi);
        if (probe == null)
            return null;
        probe.WaitForExit(5_000);
        return probe.ExitCode == 0 ? "winget" : null;
    }

    private static (int exitCode, string output) RunWinget(string winget, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = winget,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Windows refused the launch outright. ERROR_CANCELLED means the user dismissed the
            // elevation prompt, ERROR_ELEVATION_REQUIRED that it could not run unelevated at all;
            // both are the same thing to the person reading the screen. Returning the code lets
            // the classifier say so instead of this escaping as an unexplained crash.
            SetupLog.Write($"[RuntimeInstaller] could not start the install: win32={ex.NativeErrorCode} - {ex.Message}");
            return (ex.NativeErrorCode, ex.Message);
        }

        if (process == null)
            return (-1, "the installer did not start");

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            // Runtime download + install can take a few minutes on a slow link.
            if (!process.WaitForExit(300_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (-1, "the install timed out after 5 minutes");
            }

            var combined = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return (process.ExitCode, combined);
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}
