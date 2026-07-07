using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

public class AgentOptions
{
    public string ClaudePath { get; set; } = "claude";

    /// <summary>
    /// Default extra command-line arguments for Claude Code. The recommended default is the
    /// STANDARD command line (no <c>--dangerously-skip-permissions</c>) so a freshly configured
    /// Claude launches with the safe, permission-prompting behavior (issue #391). The
    /// "Automatic (skip permissions)" preset is opt-in via the Tools page, never the default.
    /// </summary>
    public string DefaultClaudeArgs { get; set; } = "";
    public int DefaultBufferSizeBytes { get; set; } = 2_097_152; // 2 MB
    public int GracefulShutdownTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Path to the Pi agent CLI (<c>pi.cmd</c> from <c>@earendil-works/pi-coding-agent</c>).
    /// Defaults to the standard npm global install location on Windows; users can override
    /// in config.json if pi is installed elsewhere.
    /// </summary>
    public string PiPath { get; set; } = DefaultNpmCliPath("pi");

    /// <summary>
    /// Path to the OpenAI Codex CLI. Codex ships two ways: the standalone installer drops a
    /// native <c>codex.exe</c> under <c>%LOCALAPPDATA%\Programs\OpenAI\Codex\bin</c> and adds it
    /// to PATH, while the npm package <c>@openai/codex</c> drops <c>codex.cmd</c> in the npm global
    /// directory (also on PATH). Both put <c>codex</c> on PATH, so the default is the bare command
    /// name and ExecutableResolver finds whichever is installed - exactly like opencode, grok, and
    /// cursor. The previous default hard-coded the npm <c>codex.cmd</c> path, which does not exist
    /// for standalone-installer users, so Codex sessions failed to launch. Users can override in
    /// config.json if codex is installed off PATH.
    /// </summary>
    public string CodexPath { get; set; } = "codex";

    /// <summary>
    /// Path to the Google Gemini CLI (<c>gemini.cmd</c> from <c>@google/gemini-cli</c>).
    /// Defaults to the standard npm global install location on Windows.
    /// </summary>
    public string GeminiPath { get; set; } = DefaultNpmCliPath("gemini");

    /// <summary>
    /// Path to the opencode CLI (the <c>opencode</c> binary from opencode.ai).
    /// opencode is not an npm package; its installer (curl/brew/scoop/mise) puts
    /// <c>opencode</c> on PATH, so the default relies on PATH resolution. Users can
    /// override in config.json if opencode is installed somewhere off PATH.
    /// </summary>
    public string OpenCodePath { get; set; } = "opencode";

    /// <summary>
    /// Path to the Cursor CLI agent (the <c>cursor-agent</c> binary from cursor.com).
    /// cursor-agent is not an npm package; its installer (the cursor.com install
    /// script) puts <c>cursor-agent</c> on PATH, so the default relies on PATH
    /// resolution. Users can override in config.json if cursor-agent is installed
    /// somewhere off PATH or named differently (issue #517, assumption A1).
    /// </summary>
    public string CursorPath { get; set; } = "cursor-agent";

    /// <summary>
    /// Path to the xAI Grok CLI (<c>grok</c> binary).
    /// The Grok installer places <c>grok.exe</c> under <c>~/.grok/bin/</c> and adds that
    /// directory to the user PATH, so the default relies on PATH resolution. Users can
    /// override in config.json if grok is installed to a non-standard location.
    /// </summary>
    public string GrokPath { get; set; } = "grok";

    /// <summary>
    /// Path to the GitHub Copilot CLI (<c>copilot.cmd</c> from <c>@github/copilot</c>).
    /// The npm global install drops <c>copilot</c>, <c>copilot.cmd</c>, and <c>copilot.ps1</c> in
    /// <c>%APPDATA%\npm</c>; the launchable shim for a process spawner is <c>copilot.cmd</c> (the
    /// <c>.ps1</c> cannot be spawned directly), so the default resolves the npm-global
    /// <c>copilot.cmd</c>. Users can override in config.json if copilot is installed elsewhere
    /// (Homebrew, WinGet, the gh.io/copilot-install script) (issue #625).
    /// </summary>
    public string CopilotPath { get; set; } = DefaultNpmCliPath("copilot");

    /// <summary>
    /// GitHub Copilot authentication token, injected into a Copilot session's environment when set
    /// (issue #625). Loaded from config.json "agent.copilot_github_token" first. The effective
    /// token resolves with the precedence Copilot itself honors:
    /// <c>COPILOT_GITHUB_TOKEN</c> &gt; <c>GH_TOKEN</c> &gt; <c>GITHUB_TOKEN</c> (see
    /// <see cref="ResolveCopilotToken"/>). Null/empty means Director injects nothing and the user
    /// completes interactive auth (<c>copilot login</c> / the <c>/login</c> slash command) inside
    /// the session tab. Never logged.
    /// </summary>
    public string? CopilotGitHubToken { get; set; }

    /// <summary>
    /// Cursor authentication key, injected into a Cursor session's environment as
    /// <c>CURSOR_API_KEY</c> when set (issue #517, assumption A5). Loaded from
    /// config.json "agent.cursor_api_key" first, then falls back to the
    /// <c>CURSOR_API_KEY</c> environment variable. Null/empty means Director injects
    /// nothing and cursor-agent uses whatever key is already in the environment.
    /// Never logged.
    /// </summary>
    public string? CursorApiKey { get; set; }

    /// <summary>
    /// Absolute path to the repository the Director chat will relay every chat
    /// message to. Set via appsettings.json "Chat.SessionRepoPath" - e.g.
    /// "C:/repos/private" - so the Director's /chat endpoint knows which
    /// session represents "the agent" for one-session deployments.
    /// Null means the Director chat will require an explicit SessionId per request.
    /// </summary>
    public string? ChatSessionRepoPath { get; set; }

    /// <summary>
    /// TTS voice for the voice mode.  Defaults to Kokoro's "af_bella".  Both the web
    /// voice page and the mobile client POST /tts with no voice override, so this
    /// single default is the voice every client speaks with. The live voice list comes
    /// from the DevThrottle proxy's speech-model catalog.
    /// </summary>
    public string TtsVoice { get; set; } = "af_bella";

    /// <summary>
    /// TTS model for Phase 3 of the voice mode.  Defaults to "hexgrad/Kokoro-82M".
    /// </summary>
    public string TtsModel { get; set; } = "hexgrad/Kokoro-82M";

    /// <summary>
    /// Path to the user-editable dictation dictionary YAML. If null, resolves
    /// to <c>%LOCALAPPDATA%/cc-director/dictation/dictionary.yaml</c>. Missing
    /// file means no vocabulary bias and no cleanup glossary; the rest of the
    /// dictation pipeline still works.
    /// </summary>
    public string? DictationDictionaryPath { get; set; }

    /// <summary>
    /// Chat model used by the dictation library's CleanupOrchestrator to propose
    /// dictionary corrections. Defaults to the DevThrottle proxy fast model; the
    /// user can override it with any model the proxy serves.
    /// </summary>
    public string DictationCleanupModel { get; set; } = TranscriptionEndpointResolver.DevThrottleWingmanFastModel;

    /// <summary>
    /// Resolve the effective dictation dictionary path. Always returns a
    /// concrete path; callers should treat a missing file as "empty dictionary".
    /// </summary>
    public string ResolveDictationDictionaryPath()
    {
        if (!string.IsNullOrWhiteSpace(DictationDictionaryPath))
            return DictationDictionaryPath;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-director", "dictation", "dictionary.yaml");
    }

    /// <summary>
    /// Resolve a locally-available DevThrottle key for the desktop voice / text-to-speech availability
    /// checks (the local key vault, <see cref="TranscriptionEndpointResolver.DevThrottleKeyName"/>).
    /// Returns null when none is set. This is the standalone/local view; a Gateway-attached Director
    /// resolves the key remotely through <see cref="TranscriptionKeyResolver"/>.
    /// </summary>
    public string? ResolveLocalTranscriptionKey()
    {
        var local = new CcDirector.Core.KeyVault().Get(TranscriptionEndpointResolver.DevThrottleKeyName);
        return string.IsNullOrWhiteSpace(local) ? null : local.Trim();
    }

    /// <summary>
    /// Resolve the effective Cursor API key: explicit config wins, then the
    /// <c>CURSOR_API_KEY</c> environment variable. Returns null if neither is set,
    /// in which case Director injects nothing (issue #517, assumption A5).
    /// </summary>
    public string? ResolveCursorApiKey()
    {
        if (!string.IsNullOrWhiteSpace(CursorApiKey))
            return CursorApiKey.Trim();
        var env = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>
    /// Resolve the effective GitHub Copilot token (issue #625). Explicit config wins, then the
    /// environment variables in the precedence Copilot itself honors:
    /// <c>COPILOT_GITHUB_TOKEN</c> &gt; <c>GH_TOKEN</c> &gt; <c>GITHUB_TOKEN</c>. Returns null when
    /// none is set, in which case Director injects nothing and the user completes interactive auth.
    /// The token value is never logged.
    /// </summary>
    public string? ResolveCopilotToken()
    {
        if (!string.IsNullOrWhiteSpace(CopilotGitHubToken))
            return CopilotGitHubToken.Trim();

        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static string DefaultNpmCliPath(string binName)
    {
        // Windows npm global install: %APPDATA%\npm\<bin>.cmd
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            var path = Path.Combine(appData, "npm", binName + ".cmd");
            FileLog.Write($"[AgentOptions] DefaultNpmCliPath({binName}): resolved from %APPDATA% to {path}");
            return path;
        }
        FileLog.Write($"[AgentOptions] DefaultNpmCliPath({binName}): %APPDATA% unavailable, falling back to bare '{binName}' (relying on PATH)");
        return binName;
    }
}
