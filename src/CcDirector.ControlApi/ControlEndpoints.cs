using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi.Chat;
using CcDirector.Core.Account;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Fleet;
using CcDirector.Core.History;
using CcDirector.Core.Network;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the Director's Control API endpoints onto the provided IEndpointRouteBuilder.
/// Now serves both REST JSON and a self-contained HTML Director UI.
/// </summary>
internal static class ControlEndpoints
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, string directorId, string version, Func<Task> requestShutdownAsync, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null, TurnSummaryCache? turnSummaryCache = null, string? gatewayUrl = null, ProactiveExplainService? proactiveExplain = null, GatewayConnectionMonitor? gatewayMonitor = null, Func<TailnetEndpointResolution>? resolveTailnetEndpoint = null, Func<GatewayClient?>? gatewayClientProvider = null, MessageSteward? messageSteward = null, MissionStore? missionStore = null, Func<CancellationToken, Task<SignedInUser?>>? signedInUserResolver = null)
    {
        // ===== Healthz =====
        app.MapGet("/healthz", () => Results.Json(new HealthDto
        {
            Status = "ok",
            Directors = 1,
            Sessions = sessionManager.ListSessions().Count,
            Version = version,
            ServerTime = DateTime.UtcNow,
            DirectorId = directorId,
            MachineName = Environment.MachineName,
        }));

        // The launch-time "fleet awareness" preamble for a session: its own identity plus the
        // cc-devthrottle commands to reach the rest of the fleet. The Claude SessionStart hook fetches this
        // and injects it as additionalContext so the agent knows the fleet instantly, with no
        // skill lookup. Plain text (not JSON) so a hook can drop it straight into a context field.
        app.MapGet("/sessions/{sid}/fleet-preamble", async (string sid, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            // Issue #800: the display name goes through the single composer so it is never the
            // bare folder name (legacy sessions with no CustomName get folder + type + disambiguator).
            var name = SessionName.DisplayName(session.CustomName,
                SessionName.FolderName(session.RepoPath),
                SessionName.Disambiguator(session.Id));

            // Issue #1357: name the signed-in DevThrottle user so the agent binds "me / my account /
            // email me" to that account. Resolved (cached) from the Gateway; null when no one is signed
            // in or no resolver is wired (tests, standalone) - the preamble then omits the identity line.
            SignedInUser? user = signedInUserResolver is null ? null : await signedInUserResolver(ct);

            // A session only ever calls its OWN Director, so this Director's machine name is the
            // session's machine.
            var text = FleetPreamble.Build(session.Id.ToString(), name, Environment.MachineName, session.RepoPath, user);
            return Results.Text(text, "text/plain");
        });

        // The fleet preamble pre-wrapped as ready-to-print SessionStart hook output. The
        // macOS/Linux shell hook cannot safely BUILD JSON (escaping arbitrary preamble text in
        // POSIX shell), so the Director serializes the whole hookSpecificOutput envelope and the
        // script just prints this response body to stdout. Empty body when there is no preamble,
        // so the hook emits nothing rather than an empty envelope.
        app.MapGet("/sessions/{sid}/fleet-preamble-hook-output", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var name = SessionName.DisplayName(session.CustomName,
                SessionName.FolderName(session.RepoPath),
                SessionName.Disambiguator(session.Id));

            var text = FleetPreamble.Build(session.Id.ToString(), name, Environment.MachineName, session.RepoPath);
            if (string.IsNullOrWhiteSpace(text))
                return Results.Text("", "text/plain");

            return Results.Json(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "SessionStart",
                    additionalContext = text,
                },
            });
        });

        // A Claude SessionStart hook (matchers startup/resume/clear/compact) reports the CURRENT
        // Claude session id + transcript path here. This is the authoritative, push-based pointer
        // update that keeps the Director tracking the right transcript across /clear and
        // auto-compaction, instead of the best-effort relink scan above. The hook script swallows
        // all errors, so this endpoint just records what it is given and returns 200. Accepts both
        // the mapped camelCase body (Windows PowerShell hook) and Claude's raw snake_case hook
        // event (forwarded verbatim by the macOS/Linux shell hook) - see ClaudeHookEventParser.
        app.MapPost("/sessions/{sid}/claude-hook", async (string sid, HttpContext httpCtx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            string body;
            using (var reader = new StreamReader(httpCtx.Request.Body))
                body = await reader.ReadToEndAsync();

            var req = ClaudeHookEventParser.Parse(body);
            if (req is null)
                return Results.BadRequest(new { error = "invalid json body" });

            FileLog.Write($"[ControlEndpoints] POST claude-hook: sid={guid} event={req?.HookEvent} source={req?.Source} claudeId={req?.ClaudeSessionId} transcript={req?.TranscriptPath}");
            session.UpdateClaudeSessionPointer(req?.ClaudeSessionId, req?.TranscriptPath, req?.Source);

            // Keep the SessionManager's claude-id routing map in sync with the new id.
            if (!string.IsNullOrWhiteSpace(req?.ClaudeSessionId))
                sessionManager.RelinkClaudeSession(guid, req!.ClaudeSessionId!);

            return Results.Json(new { received = true });
        });

        // ===== Shutdown =====
        app.MapPost("/shutdown", (HttpContext ctx) =>
        {
            // Name the local caller (issue #212 L3). This endpoint stops the whole Director
            // and every claude.exe under it; the 2026-06-06 post-mortem could not tell whether
            // an agent had triggered a shutdown because the caller was never recorded.
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] POST shutdown requested caller={caller}");
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                try { await requestShutdownAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlEndpoints] Shutdown FAILED: {ex.Message}"); }
            });
            return Results.Json(new { accepted = true });
        });

    }

    /// <summary>Folder name of a repo path, for display fallback. Empty path -> "Unknown Project".</summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's claude-sessions core
    // (lifted verbatim from GET /claude-sessions) calls the SAME helper - no drift, no duplication.
    internal static string ProjectNameOf(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath))
            return "Unknown Project";
        return Path.GetFileName(repoPath.TrimEnd('\\', '/'));
    }

    /// <summary>
    /// Canonical form for repo-path comparison across endpoints (?repo= filters, overview
    /// grouping): full path, trailing separators trimmed, lowercased (Windows paths are
    /// case-insensitive). Callers must filter null/empty first; Path.GetFullPath throws
    /// only on empty or embedded-NUL input, which the global error envelope surfaces loudly.
    /// </summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's claude-sessions core
    // (lifted verbatim from GET /claude-sessions) calls the SAME helper - no drift, no duplication.
    internal static string NormalizeRepoPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// List sub-directories of <paramref name="path"/> for the remote folder browser. A null or
    /// empty path returns the drive roots. Solo-tailnet: no path sandboxing (see remote-experience-plan.md).
    /// </summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's fs-list core (the
    // list-directory read, lifted from GET /fs/list) calls the SAME helper - no drift, no duplication.
    internal static DirectoryListingDto ListDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new DirEntryDto
                {
                    Name = d.Name.TrimEnd('\\', '/'),
                    Path = d.RootDirectory.FullName,
                    IsDrive = true,
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new DirectoryListingDto { CurrentPath = null, ParentPath = null, Entries = drives };
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"directory not found: {full}");

        var parent = Directory.GetParent(full.TrimEnd('\\', '/'))?.FullName;

        var entries = Directory.EnumerateDirectories(full)
            .Select(d =>
            {
                try
                {
                    // Skip hidden / system directories so the picker stays clean.
                    var attr = File.GetAttributes(d);
                    if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                        return null;
                }
                catch { return null; }
                return new DirEntryDto { Name = Path.GetFileName(d), Path = d, IsDrive = false };
            })
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DirectoryListingDto { CurrentPath = full, ParentPath = parent, Entries = entries };
    }

    /// <summary>
    /// The session's ENTIRE terminal buffer, ANSI stripped, for the "Ask the Wingman"
    /// answer path. Unlike WingmanContextBuilder's tail (capped at 4000 chars for a
    /// one-shot prompt), this returns everything: the read-only session writes it to a
    /// snapshot file and reads only as much as it needs, so "read me the whole article"
    /// can reach content that scrolled past a tail. Read-only inspection; never mutates.
    /// </summary>
    internal static string ReadFullCleanedBuffer(Session session)
    {
        try
        {
            var bytes = session.Buffer?.DumpAll();
            if (bytes is null || bytes.Length == 0) return "";
            return AnsiCleaner.Clean(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlEndpoints] ReadFullCleanedBuffer FAILED: {ex.Message}");
            return "";
        }
    }

    /// <summary>The session driver's capability flags as names, for UIs to render
    /// action buttons from (no per-agent special cases client-side).</summary>
    private static List<string> CapabilityNames(Session s)
    {
        var caps = s.Driver.Capabilities;
        return Enum.GetValues<Core.Drivers.DriverCapabilities>()
            .Where(f => f != Core.Drivers.DriverCapabilities.None && caps.HasFlag(f))
            .Select(f => f.ToString())
            .ToList();
    }

    /// <summary>
    /// Map a session to its DTO. Issue #335: machineName, user, tailnetEndpoint, and viewUrl
    /// are now populated by the Director itself (not patched in by the Gateway), so every
    /// consumer of the Director-local /sessions and /sessions/{sid} endpoints always sees
    /// the four identity fields. The Gateway aggregation pass still enriches DTOs from OLD
    /// Directors that send empty fields (back-compat for mixed-version fleets) but must NOT
    /// overwrite non-empty Director-supplied values.
    /// </summary>
    // Issue #1176 (Phase 1a): internal so the Director's stream client builds its pushed snapshots and
    // deltas through the EXACT same mapper the local /sessions endpoint uses (review #6), rather than a
    // second, divergent builder. Callers outside /sessions pass only (session, directorId); the Gateway
    // aggregator stamps machine/user/tailnet/view-url during aggregation, for pushed and pulled alike.
    internal static SessionDto Map(Session s, string directorId, TurnSummaryCache? cache = null,
        string machineName = "", string user = "", string tailnetEndpoint = "", string? gatewayUrl = null)
    {
        // Phase 3: StatusColor and LastStatusReason are owned by the SessionStatusWingman
        // and live on the Session itself. Map() reads them directly - no derivation, no
        // recomputation from TurnSummaryCache, no fallback. The `cache` argument is kept for
        // other endpoints that surface raw summaries; it is not consulted for color.
        // Idle clock source. Most agents go byte-silent when idle, so the raw last-write time is
        // the honest "how long since output" measure. Agents with an animated idle footer (Grok)
        // never go byte-silent - their footer repaints forever - so the raw clock would read ~0
        // even when the agent is done and waiting. For those, measure idle from the last screen-
        // BODY change instead (Session.LastBodyActivityAtUtc), matching the detector's content rule
        // so the idle seconds and the WaitingForInput badge agree.
        DateTime lastActivity;
        if (s.Driver.EmitsContinuousIdleOutput)
        {
            lastActivity = s.LastBodyActivityAtUtc;
        }
        else
        {
            var lastWrite = s.Buffer?.LastWriteAtUtc ?? DateTime.MinValue;
            lastActivity = lastWrite == DateTime.MinValue ? s.CreatedAt.UtcDateTime : lastWrite;
        }
        var idleSeconds = Math.Max(0, (DateTime.UtcNow - lastActivity).TotalSeconds);

        // Issue #335: build the viewUrl from the resolved tailnetEndpoint and the configured
        // gatewayUrl. Format: {tailnetEndpoint}/sessions/{sid}/view?gw={gatewayBase}
        // Only set when a real (non-empty) tailnetEndpoint resolved - never a loopback lie.
        var viewUrl = "";
        if (!string.IsNullOrEmpty(tailnetEndpoint))
        {
            var sidStr = s.Id.ToString();
            var base64 = tailnetEndpoint.TrimEnd('/');
            viewUrl = !string.IsNullOrEmpty(gatewayUrl)
                ? $"{base64}/sessions/{sidStr}/view?gw={Uri.EscapeDataString(gatewayUrl)}"
                : $"{base64}/sessions/{sidStr}/view";
        }

        return new()
        {
            SessionId = s.Id.ToString(),
            DirectorId = directorId,
            Agent = s.AgentKind.ToString(),
            GroupId = s.GroupId?.ToString(),
            GroupRole = s.GroupRole,
            RepoPath = s.RepoPath,
            Status = s.Status.ToString(),
            ActivityState = s.ActivityState.ToString(),
            // SessionDto.AssessedState is intentionally left null (defaults null): the Gateway-pushed
            // "assessed state" display annotation (issue #186) was retired with the Director's overlay
            // fold (issue #1177, Phase 2.3). Readers still do "AssessedState ?? ActivityState", so a null
            // here means they fall through to the raw ActivityState - the documented steady state.
            CreatedAt = s.CreatedAt.UtcDateTime,
            TotalBufferBytes = s.Buffer?.TotalBytesWritten ?? 0,
            IsAlternateScreen = s.IsAlternateScreen,
            ClaudeSessionId = s.ClaudeSessionId,
            ClaudeTranscriptPath = s.ClaudeTranscriptPath,
            BackendType = s.BackendType.ToString(),
            DriverCapabilities = CapabilityNames(s),
            Name = s.CustomName,
            Number = s.Number,
            // Mission attachment (mission-as-first-class-unit-of-work): the link + cached display name flow
            // straight through the Gateway aggregation on the SessionDto; the RESOLVED Mission record lives
            // in the Director's MissionStore.
            MissionId = s.MissionId,
            MissionName = s.MissionName,
            SortOrder = s.SortOrder,
            StatusColor = s.StatusColor,
            LastStatusReason = s.LastStatusReason,
            BriefingState = s.BriefingState.ToString(),
            RailLine = s.LatestBriefRailLine,
            LastActivityAt = lastActivity,
            IdleSeconds = idleSeconds,
            QuietThresholdSeconds = CcDirector.Core.Wingman.TerminalStateDetector.QuietThreshold.TotalSeconds,
            VoiceMode = s.VoiceMode,
            OnHold = s.OnHold,
            PendingDeletion = s.PendingDeletion,
            DeletionReason = s.DeletionReason,
            WingmanEnabled = s.WingmanEnabled,
            // Scheduled-run auto-dismiss (issue #1200): the flag + the agent's parsed verdict ride the
            // snapshot/delta path up to the Gateway's auto-dismiss sweep, which closes the session over the
            // stream on "done". Reported straight from the Session; the Gateway is the actor.
            AutoDismiss = s.AutoDismiss,
            DismissVerdict = s.DismissVerdict,
            // Raw local facts for the Gateway color fold (issue #1177, Phase 2). Reported straight from
            // the Session; the Director does NOT fold them into a color here (StatusColor is unchanged).
            IsBrandNew = s.IsBrandNew,
            IsControlled = s.IsControlled,
            ControllerSessionId = s.ControllerSessionId?.ToString(),
            // Automatic session roles (chunk 2.5): the sticky explicit role, so the Gateway aggregation can
            // apply the explicit-wins precedence. The RESOLVED SessionRole is computed at the aggregation.
            ExplicitRole = s.ExplicitRole,
            // Chunk 3: the auto-vs-explicit name marker (a future auto-rename gates on it).
            IsAutoNamed = s.IsAutoNamed,
            IsBackgroundRunning = s.IsBackgroundRunning,
            // Issue #1177 (Phase 2): the two Director-baked overlays that previously reached the Gateway
            // ONLY via the cooked StatusColor. Now reported as raw facts so the Gateway fold reproduces
            // them (desktop-dictation orange, legacy auto-explain yellow) without reading StatusColor.
            IsTranscribing = s.IsTranscribing,
            IsAutoExplaining = s.IsExplaining,
            // DevThrottle Stats: the per-session input tally, taken at the choke point. Null when empty so a
            // fresh session does not bloat every snapshot; the Gateway aggregates the non-null tallies.
            InputStats = s.InputStats.IsEmpty ? null : s.InputStats.Snapshot(),
            RemoteRepo = s.RemoteRepo ?? "",
            RemoteThreadUrl = s.RemoteThreadUrl ?? "",
            RemoteRunUrl = s.RemoteRunUrl ?? "",
            RemoteRunStatus = s.RemoteRunStatus ?? "",
            // Issue #335: identity fields populated by the Director (not patched by the Gateway).
            MachineName = machineName,
            User = user,
            TailnetEndpoint = tailnetEndpoint,
            ViewUrl = viewUrl,
        };
    }

    /// <summary>
    /// Convert the agent-agnostic conversation history into the legacy turn widget shape consumed by
    /// the Gateway Wingman voice path. Assistant text must stay Kind="Text": that is the contract
    /// WingmanTranslator uses to find the latest reply to summarize and speak.
    /// </summary>
    // Internal (not private) so the extracted SessionReadExecutor.Turns core can call the SAME widget
    // builder the REST route used, keeping the tunnel verb and the route byte-identical (Gateway Cleanup
    // mission, Phase 0).
    internal static List<TurnWidgetDto> BuildTurnWidgetsFromHistory(ConversationHistory history)
    {
        var widgets = new List<TurnWidgetDto>();
        foreach (var message in history.Messages)
        {
            foreach (var part in message.Parts)
            {
                var text = part.Text?.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                widgets.Add(part.Kind switch
                {
                    ConversationPartKind.Text when message.Role == ConversationRole.Assistant => new TurnWidgetDto
                    {
                        Kind = "Text",
                        Header = "Agent",
                        Content = text,
                    },
                    ConversationPartKind.Text => new TurnWidgetDto
                    {
                        Kind = "UserMessage",
                        Header = "You",
                        Content = text,
                    },
                    ConversationPartKind.Thinking => new TurnWidgetDto
                    {
                        Kind = "Thinking",
                        Header = "Thinking",
                        Content = text,
                    },
                    ConversationPartKind.ToolUse => new TurnWidgetDto
                    {
                        Kind = "GenericTool",
                        Header = part.ToolName ?? "Tool",
                        Content = text,
                        ToolUseId = part.ToolId ?? "",
                    },
                    ConversationPartKind.ToolResult => new TurnWidgetDto
                    {
                        Kind = "GenericTool",
                        Header = "Tool result",
                        Content = text,
                        ToolUseId = part.ToolId ?? "",
                    },
                    _ => new TurnWidgetDto
                    {
                        Kind = part.Kind.ToString(),
                        Header = part.Kind.ToString(),
                        Content = text,
                    },
                });
            }
        }

        return widgets;
    }

    /// <summary>
    /// Compute the current turn count from the session's linked JSONL file.
    /// Returns 0 if the session isn't linked or the file isn't there yet.
    /// Used by the recap endpoints to compute the IsStale flag.
    /// </summary>
    // Gateway Cleanup Phase 0: internal so the shared SessionReadExecutor.Recap core (which the tunnel
    // recap verb and the re-pointed GET /sessions/{sid}/recap route both call) reads the SAME turn count.
    internal static int ComputeTurnCount(Session session)
    {
        if (string.IsNullOrEmpty(session.ClaudeSessionId)) return 0;
        try
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl)) return 0;
            var messages = StreamMessageParser.ParseFile(jsonl);
            return WidgetBuilder.BuildFromMessages(messages).Count;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlEndpoints] ComputeTurnCount FAILED: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Only allow same-origin path redirects (defense against open-redirect).</summary>
    private static bool IsSafeRedirect(string next)
    {
        return !string.IsNullOrEmpty(next)
            && next.StartsWith("/", StringComparison.Ordinal)
            && !next.StartsWith("//", StringComparison.Ordinal);
    }

    // ===== Screenshots gallery helpers =====

    private static readonly string[] ScreenshotExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>
    /// Newest-N cap applied to GET /screenshots when the client sends no ?count. The gallery
    /// only ever shows the newest few screenshots; older files are deliberately never listed.
    /// Internal (not private) so the endpoint tests assert against the same number.
    /// </summary>
    internal const int DefaultScreenshotCount = 100;

    /// <summary>Image files in the screenshots folder, newest first. Empty if the folder is missing.</summary>
    /// <summary>
    /// All image files in the screenshots folder, newest first, in ONE filesystem pass.
    /// DirectoryInfo.EnumerateFiles yields FileInfo objects pre-populated from the directory
    /// enumeration data, so sorting and reading LastWriteTime/Length costs no extra stat
    /// calls. The previous string-path version stat'ed every file twice (sort + FileInfo),
    /// which took ~100ms on a 1400-file folder; this takes ~1-2ms.
    /// </summary>
    // Internal (Gateway Cleanup Phase 0) so the screenshots-list byte verb enumerates the same folder this
    // REST route does - one enumerator, no drift.
    internal static List<FileInfo> ScreenshotFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return new List<FileInfo>();
        return new DirectoryInfo(directory)
            .EnumerateFiles()
            .Where(f => ScreenshotExtensions.Contains(f.Extension.ToLowerInvariant()))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
    }

    /// <summary>
    /// Resolve a client-supplied screenshot name to an absolute path INSIDE the screenshots
    /// folder, or null if it escapes the folder, is not a bare file name, has a non-image
    /// extension, or does not exist. Defends GET/DELETE /screenshots/file against traversal.
    /// </summary>
    // Internal (Gateway Cleanup Phase 0) so the screenshot-file stream verb resolves the same traversal-safe
    // path this REST route does - one resolver, no drift.
    internal static string? ResolveScreenshot(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        // Reject anything that isn't a bare file name (no separators, no "..").
        if (Path.GetFileName(name) != name)
            return null;
        if (!ScreenshotExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            return null;

        var dir = CcStorage.Screenshots();
        var full = Path.GetFullPath(Path.Combine(dir, name));
        // Confirm the resolved path is still under the screenshots folder.
        var dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    internal static string ScreenshotContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// The single extension -> HTTP content-type map shared by the top-level <c>GET /file</c> and the
    /// session-scoped <c>GET /sessions/{sid}/file</c> (Local Files mission). One map, two routes, so a
    /// new type is added in exactly one place. The octet-stream fallback means an unknown extension is
    /// served as an opaque download, never guessed as text or an image.
    /// </summary>
    internal static string FileContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".pdf"            => "application/pdf",
        ".png"            => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif"            => "image/gif",
        ".svg"            => "image/svg+xml",
        ".css"            => "text/css; charset=utf-8",
        ".js"             => "text/javascript; charset=utf-8",
        ".json"           => "application/json; charset=utf-8",
        ".csv"            => "text/csv; charset=utf-8",
        ".md" or ".txt" or ".log" => "text/plain; charset=utf-8",
        _                 => "application/octet-stream",
    };
}
