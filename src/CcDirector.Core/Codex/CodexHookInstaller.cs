using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Codex;

/// <summary>
/// Installs the Codex SessionStart hook that surfaces the fleet preamble into a Codex session's
/// context, the Codex analogue of <see cref="Claude.ClaudeHookInstaller"/>.
///
/// Codex fires a <c>SessionStart</c> hook with a <c>source</c> matcher (startup/resume/clear/compact)
/// whose command may print <c>hookSpecificOutput.additionalContext</c> - the same shape Claude uses.
/// Unlike Claude (which takes a private settings file via <c>--settings</c>), Codex reads hooks only
/// from fixed locations, so this MERGES our SessionStart entry into the user-layer
/// <c>~/.codex/hooks.json</c> (honoring <c>CODEX_HOME</c>) without disturbing the user's own hooks.
/// The hook script is static and shared: it reads the per-session <c>CC_SESSION_PREAMBLE_FILE</c> the
/// Director injects, so it no-ops in the user's own (non-Director) Codex sessions. The Director appends
/// <c>--dangerously-bypass-hook-trust</c> at launch so the hook runs without a per-user trust prompt
/// (verified live on codex 0.141.0).
///
/// Remove-the-network-port mission, phase 3: this used to GET the preamble from the Director's Control
/// API and wrap it as hook output itself. It now prints the file the Director MAINTAINS for the session,
/// which is already the finished envelope - so Codex and Claude receive the identical bytes from the
/// identical file, and neither the port nor a credential is involved.
/// </summary>
public static class CodexHookInstaller
{
    // PowerShell: print the preamble file the Director maintains for this session. The hook does not
    // need the event payload, so it deliberately does not read stdin; Codex 0.142+ can fail interactive
    // startup if a hook command consumes or probes the terminal stdin. Must never block or fail the
    // session - swallows all errors and exits 0. Codex re-fires SessionStart on /clear and /compact,
    // and the Director keeps the file current, so a clear hours after launch injects the user's CURRENT
    // text rather than a launch-time snapshot. An empty or absent file means inject nothing.
    private const string ScriptContent =
        "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
        "try {\r\n" +
        "    $pre = $env:CC_SESSION_PREAMBLE_FILE\r\n" +
        "    if ($pre -and (Test-Path -LiteralPath $pre)) {\r\n" +
        "        $out = [System.IO.File]::ReadAllText($pre)\r\n" +
        "        if ($out) { [Console]::Out.Write($out) }\r\n" +
        "    }\r\n" +
        "} catch { }\r\n" +
        "exit 0\r\n";

    /// <summary>The SessionStart sources Codex can switch context on - the moments we want the
    /// preamble (re-)injected. Same set Claude uses.</summary>
    private const string Matcher = "startup|resume|clear|compact";

    /// <summary>The launch flag the Director appends so the hook runs without a per-user trust
    /// prompt. Exposed so SessionManager and tests share one source of truth.</summary>
    public const string BypassTrustFlag = "--dangerously-bypass-hook-trust";

    /// <summary>
    /// Ensure the hook script exists and our SessionStart entry is present in the user's Codex
    /// hooks.json. Returns true on success (the Director should then append
    /// <see cref="BypassTrustFlag"/> to the Codex command); false if anything failed, in which case
    /// the session still launches, just without the preamble hook.
    /// </summary>
    public static bool EnsureInstalled() => EnsureInstalled(DefaultScriptDirectory(), DefaultCodexHooksPath());

    /// <summary>Testable overload that writes under explicit paths.</summary>
    public static bool EnsureInstalled(string scriptDirectory, string hooksJsonPath)
    {
        try
        {
            Directory.CreateDirectory(scriptDirectory);
            var scriptPath = Path.Combine(scriptDirectory, "report-preamble.ps1");
            File.WriteAllText(scriptPath, ScriptContent);

            var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
            MergeSessionStartHook(hooksJsonPath, command);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodexHookInstaller] EnsureInstalled failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Add our SessionStart command hook to <paramref name="hooksJsonPath"/>, preserving any hooks
    /// the user already has. Idempotent: a re-run with the same script command is a no-op. Writes
    /// atomically (temp file then replace) so a crash mid-write cannot corrupt the user's hooks.
    /// </summary>
    private static void MergeSessionStartHook(string hooksJsonPath, string command)
    {
        var root = LoadRoot(hooksJsonPath);

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        if (hooks["SessionStart"] is not JsonArray sessionStart)
        {
            sessionStart = new JsonArray();
            hooks["SessionStart"] = sessionStart;
        }

        // Idempotent: if any existing entry already runs our command, leave the file untouched.
        foreach (var entry in sessionStart)
        {
            if (entry is JsonObject obj && obj["hooks"] is JsonArray inner)
            {
                foreach (var h in inner)
                {
                    if (h is JsonObject ho && ho["command"]?.GetValue<string>() == command)
                        return;
                }
            }
        }

        sessionStart.Add(new JsonObject
        {
            ["matcher"] = Matcher,
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["timeout"] = 10,
            }),
        });

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(hooksJsonPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = hooksJsonPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, hooksJsonPath, overwrite: true);
    }

    private static JsonObject LoadRoot(string hooksJsonPath)
    {
        if (!File.Exists(hooksJsonPath))
            return new JsonObject();

        var existing = File.ReadAllText(hooksJsonPath);
        if (string.IsNullOrWhiteSpace(existing))
            return new JsonObject();

        // A malformed user hooks.json must not be clobbered: surface the error so EnsureInstalled
        // returns false and the session launches without the hook, rather than overwriting it.
        return JsonNode.Parse(existing) as JsonObject
            ?? throw new InvalidOperationException($"hooks.json is not a JSON object: {hooksJsonPath}");
    }

    private static string DefaultScriptDirectory() => CcStorage.CodexHooks();

    private static string DefaultCodexHooksPath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(codexHome, "hooks.json");
    }
}
