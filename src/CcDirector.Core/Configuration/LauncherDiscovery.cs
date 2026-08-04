using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The launcher presence fact (issue #330), read from the registration file the RUNNING launcher writes.
/// <see cref="Installed"/> is the file existing; <see cref="Pid"/> and <see cref="Version"/> identify the
/// process that wrote it. Whether that process is STILL running is a separate question - ask
/// <see cref="LauncherDiscovery.IsRunning"/> - because a crashed launcher leaves its file behind.
/// </summary>
public sealed record LauncherFact(bool Installed, int? Pid, string? Version, string? Error);

/// <summary>
/// Reads and writes the launcher registration file
/// (<c>%LOCALAPPDATA%/cc-director/config/launcher/launcher.json</c>).
///
/// Remove-the-network-port mission, phase 6: this file used to be a DISCOVERY file - {port, token, pid} -
/// so an agent or the Gateway could dial the launcher's loopback REST interface. That interface is gone;
/// the launcher listens on nothing. The file is now the launcher twin of the Director's instance
/// registration: the fact the RUNNING PROCESS writes about itself - {pid, version, startedAtUtc,
/// userInterface, autostart state} - written on startup, rewritten when the autostart state changes, and
/// deleted on clean shutdown. It is how anything local (the Director's update fold, the installer's
/// readiness wait, the self-update helper) answers "is a launcher up, and WHICH process is it?" without a
/// socket. Reader and writer live in one class so the field names cannot drift apart.
///
///   - File absent  -> Installed=false (a VALID fact: no launcher is running, or none has shipped).
///   - File present -> Installed=true + the writing process's pid and version.
///   - File present but corrupt -> Installed=true, Pid=null, Error names why (the file existing IS the
///     presence fact; an unreadable identity must not masquerade as "not installed").
///
/// Read at request time, never cached - the launcher may start/stop while the Director runs.
/// </summary>
public static class LauncherDiscovery
{
    /// <summary>The production registration file location (mirrors InstanceRegistration's layout).</summary>
    public static string DefaultPath { get; } =
        Path.Combine(CcStorage.ToolConfig("launcher"), "launcher.json");

    /// <summary>Read the launcher fact. Tests pass an isolated path; production omits it.</summary>
    public static LauncherFact Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new LauncherFact(Installed: false, Pid: null, Version: null, Error: null);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            FileLog.Write($"[LauncherDiscovery] Read FAILED (file present but unreadable): {path}: {ex.Message}");
            return new LauncherFact(Installed: true, Pid: null, Version: null, Error: $"launcher.json unreadable: {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            int? pid = null;
            string? version = null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("pid", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var parsedPid))
                    pid = parsedPid;
                else if (property.Name.Equals("version", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                    version = property.Value.GetString();
            }
            return pid is null
                ? new LauncherFact(Installed: true, Pid: null, Version: version, Error: "launcher.json has no pid field")
                : new LauncherFact(Installed: true, Pid: pid, Version: version, Error: null);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[LauncherDiscovery] Read: corrupt launcher.json at {path}: {ex.Message}");
            return new LauncherFact(Installed: true, Pid: null, Version: null, Error: $"launcher.json unparsable: {ex.Message}");
        }
    }

    /// <summary>
    /// Is the process that wrote this registration still alive? A registration with no readable pid is
    /// NOT running - identity that cannot be checked must not pass for health, which is the same rule the
    /// installer's old port probe followed when an answer carried no process id.
    /// </summary>
    public static bool IsRunning(LauncherFact fact)
    {
        if (fact is not { Installed: true, Pid: int pid }) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // no such process - the registration is stale (crash, kill)
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherDiscovery] IsRunning: could not read process {pid}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Write the registration for the CURRENT process. Called by the launcher on startup and again
    /// whenever the autostart state changes, so the file always describes the running launcher.
    /// </summary>
    public static void Write(string version, string userInterfaceState,
        bool autostartChecked, bool autostartRegistered, string? autostartFailure, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // The process's OWN start time, not "now": the file is rewritten when the autostart state
            // changes, and a rewrite must not make a long-running launcher look freshly started.
            DateTime startedAtUtc;
            try { using var self = Process.GetCurrentProcess(); startedAtUtc = self.StartTime.ToUniversalTime(); }
            catch { startedAtUtc = DateTime.UtcNow; }
            var json = JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                version,
                startedAtUtc,
                userInterface = userInterfaceState,
                // Null, not false, until the state has actually been decided - saying anything about a
                // question nobody has asked yet is the lie this field exists to remove. Registered is the
                // fleet-visible fact: autostart turned off on purpose is not a failure, but it is not
                // "managed" either.
                autostartOk = autostartChecked ? autostartFailure is null && autostartRegistered : (bool?)null,
                autostartRegistered = autostartChecked ? autostartRegistered : (bool?)null,
                autostartFailure,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.WriteAllText(path, json);
            FileLog.Write($"[LauncherDiscovery] registration written: {path} (pid={Environment.ProcessId}, version={version})");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherDiscovery] Write FAILED: {ex.Message}");
        }
    }

    /// <summary>Remove the registration on clean shutdown. A crash leaves it behind, which is why readers
    /// judge liveness by <see cref="IsRunning"/> and never by the file alone.</summary>
    public static void Delete(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            FileLog.Write("[LauncherDiscovery] registration removed");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherDiscovery] Delete FAILED: {ex.Message}");
        }
    }
}
