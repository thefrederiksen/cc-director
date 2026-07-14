using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Gemini;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>
/// Copies each session's conversation into the durable <see cref="ConversationLog"/> (issue #1551).
///
/// Trigger: the same one <see cref="TurnReviewLogger"/> uses - a session flipping to
/// <see cref="ActivityState.WaitingForInput"/>, i.e. our own detector deciding "the agent is done and
/// needs the user". That is exactly when new messages exist to copy, so there is no polling.
///
/// Source: <see cref="SessionHistoryReader"/>, the agent-neutral facade over all supported agents. We
/// keep only <see cref="ConversationPartKind.Text"/> parts - tool calls and their results are dropped
/// (they are most of the volume and not the signal), and thinking blocks with them.
///
/// Watermarking: agents rewrite and compact their transcripts, so "how many did I read last time" is
/// not durable. Instead each written message's identity (timestamp + role + text hash) is remembered
/// per session and persisted, so re-reading the same transcript cannot double-append. State lives at
/// base/prompt-log/ingest-state.json.
///
/// Read-only over sessions and fail-safe throughout: ingest must never break a turn.
/// </summary>
public sealed class ConversationIngestor : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private readonly IngestState _state = IngestState.Load();
    private bool _started;
    private int _disposed;

    public ConversationIngestor(SessionManager sessionManager) => _sessionManager = sessionManager;

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[ConversationIngestor] Start");
        _sessionManager.OnSessionCreated += WireSession;
        _sessionManager.OnSessionRemoved += UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

    private void WireSession(Session session)
    {
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (_, @new) =>
        {
            // The single trigger: the agent just finished a turn, so new messages exist to copy.
            if (@new != ActivityState.WaitingForInput) return;
            // Off the event thread - reading a transcript is disk work and must not stall the UI.
            Task.Run(() => Ingest(session));
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;
    }

    private void UnwireSession(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var h))
            session.OnActivityStateChanged -= h;
        // Deliberately keep this session's seen-set: a session can be removed and restored, and its
        // transcript survives, so forgetting here would re-append everything it ever said.
    }

    /// <summary>
    /// Copy any not-yet-recorded messages for this session. Public so a backfill can drive it directly.
    /// Never throws.
    /// </summary>
    public void Ingest(Session session)
    {
        try
        {
            if (!SessionHistoryReader.IsSupported(session)) return;

            var history = ReadForRecord(session);
            if (history.Messages.Count == 0) return;

            var scope = ScopeKey(session);

            var origins = LoadOriginsFor(session, history);
            var toWrite = new List<ConversationRecord>();

            foreach (var message in history.Messages)
            {
                var text = TextOf(message);
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Gemini carries no timestamps (its history is scraped from the terminal buffer), so
                // there is nothing real to stamp with. Record ingest time and mark it as ours, rather
                // than letting an inferred time read as a measured one.
                var fromAgent = message.Timestamp.HasValue;
                var ts = message.Timestamp?.UtcDateTime ?? DateTime.UtcNow;

                if (_state.AlreadyWritten(scope, ts, message.Role, text, fromAgent)) continue;

                var isUser = message.Role == ConversationRole.User;
                var origin = isUser ? MatchOrigin(origins, ts, fromAgent) : null;

                toWrite.Add(new ConversationRecord
                {
                    TsUtc = ts,
                    SessionId = session.Id.ToString(),
                    ContextId = ContextIdFor(session, message),
                    SessionName = session.CustomName,
                    RepoPath = session.RepoPath,
                    Agent = session.AgentKind.ToString(),
                    MissionName = session.MissionName,
                    Role = isUser ? "user" : "assistant",
                    Modality = origin?.Modality,
                    // A user message we could not attribute is "unknown", never a guess. An assistant
                    // reply has no origin at all, so it stays null.
                    Surface = isUser ? (origin?.Surface ?? "unknown") : null,
                    TimestampFromAgent = fromAgent,
                    CharCount = text.Length,
                    WordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                    Text = text,
                });

                _state.MarkWritten(scope, ts, message.Role, text, fromAgent);
            }

            if (toWrite.Count == 0) return;

            ConversationLog.WriteMany(toWrite);
            _state.Save();
            FileLog.Write($"[ConversationIngestor] session={session.Id} wrote {toWrite.Count} message(s)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConversationIngestor] Ingest FAILED (swallowed) session={session.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Which agent CONTEXT a message belonged to - what you group by to replay one conversation as the
    /// agent saw it, and what resets when the context is cleared (issue #1551).
    ///
    /// Prefer the id the source itself stamped on the message: Claude and Gemini both carry one, and
    /// only a per-message value is correct for a source that holds several contexts in one file. Fall
    /// back to the transcript file's own name, which for the file-per-context agents (Codex, Pi, Grok)
    /// IS the context's identity - a new context means a new file.
    ///
    /// Null when the agent exposes no context identity at all (Copilot and OpenCode resolve out of a
    /// shared store by repo). Recorded as absent rather than invented; the Director session id still
    /// groups the work.
    /// </summary>
    private static string? ContextIdFor(Session session, ConversationMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.ContextId)) return message.ContextId;

        return session.AgentKind switch
        {
            // These resolve out of one shared per-repo store and expose no context identity we can read.
            AgentKind.Copilot or AgentKind.OpenCode => null,
            _ => TranscriptStem(session),
        };
    }

    /// <summary>The transcript file's name without extension - the context identity for the agents that
    /// keep one file per context. Null when no transcript path resolves.</summary>
    private static string? TranscriptStem(Session session)
    {
        var path = SessionHistoryReader.ResolveTranscriptPath(session);
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// What the dedupe is scoped to: the SOURCE we read, not the Director session that triggered the
    /// read. These are not the same thing, and assuming they were is a duplication bug.
    ///
    /// Claude, Codex, Pi and Grok keep a transcript per agent session, so the source is that file and
    /// scoping by Director session happens to agree. But Copilot and OpenCode resolve their
    /// conversation by REPO out of one shared SQLite store, and Gemini's logs.json is keyed by repo
    /// too - so two Director sessions on one repo read the SAME conversation. Scoped by session, each
    /// would keep its own seen-set and write every message twice. Scoped by source, they share one.
    /// </summary>
    private static string ScopeKey(Session session) => session.AgentKind switch
    {
        AgentKind.Gemini => $"gemini|{session.RepoPath}",
        AgentKind.Copilot => $"copilot|{session.RepoPath}",
        AgentKind.OpenCode => $"opencode|{session.RepoPath}",
        // Per-session transcript agents: the file itself is the identity. Fall back to the session id
        // only if the path cannot be resolved, which is the narrowest honest scope available.
        _ => SessionHistoryReader.ResolveTranscriptPath(session) ?? session.Id.ToString(),
    };

    /// <summary>
    /// The conversation to copy for this session. Normally the agent-neutral
    /// <see cref="SessionHistoryReader"/> facade - with ONE deliberate exception.
    ///
    /// Gemini persists no transcript, so the facade builds its history by scraping the terminal
    /// scrollback into a single unstructured, untimestamped blob. That is right for the History tab
    /// (it is the only place Gemini's replies exist) and wrong here: the blob grows every turn, so it
    /// is a different message each time and copying it would append the whole conversation again on
    /// every turn, forever. Gemini's own logs.json has the user's prompts with real timestamps, which
    /// is what a durable record needs - so the record reads that instead, and honestly has no Gemini
    /// replies rather than a re-appended screen dump. See <see cref="GeminiPromptLogReader"/>.
    /// </summary>
    private static ConversationHistory ReadForRecord(Session session)
        => session.AgentKind == AgentKind.Gemini
            ? GeminiPromptLogReader.Read(session.RepoPath)
            : SessionHistoryReader.Read(session);

    /// <summary>Concatenate a message's Text parts. Tool calls, tool results, and thinking are dropped.</summary>
    private static string TextOf(ConversationMessage message)
    {
        var parts = message.Parts
            .Where(p => p.Kind == ConversationPartKind.Text)
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t));
        return string.Join("\n", parts).Trim();
    }

    /// <summary>
    /// The origin events that could plausibly belong to this session's messages: the days the history
    /// spans. Read once per ingest rather than per message.
    /// </summary>
    private static List<InputOriginRecord> LoadOriginsFor(Session session, ConversationHistory history)
    {
        var stamps = history.Messages.Where(m => m.Timestamp.HasValue)
            .Select(m => m.Timestamp!.Value.UtcDateTime).ToList();
        var from = stamps.Count > 0 ? stamps.Min().Date : DateTime.UtcNow.Date;
        var to = stamps.Count > 0 ? stamps.Max().Date : DateTime.UtcNow.Date;
        var id = session.Id.ToString();
        return InputOriginLog.Read(from, to).Where(o => o.SessionId == id).ToList();
    }

    /// <summary>
    /// The closest origin event in time to this message, or null when none is close enough. The join is
    /// deliberately tight: an origin event is written the instant a submission crosses a choke point,
    /// and the agent stamps the message on receipt, so a real pair is seconds apart. Anything further
    /// out is not evidence, and an unmatched message is honestly "unknown".
    /// </summary>
    private static InputOriginRecord? MatchOrigin(List<InputOriginRecord> origins, DateTime ts, bool timestampFromAgent)
    {
        // Without a real agent timestamp there is nothing to join on - we would be matching our own
        // ingest clock against submission times, which is not a measurement.
        if (!timestampFromAgent || origins.Count == 0) return null;

        InputOriginRecord? best = null;
        var bestDelta = TimeSpan.MaxValue;
        foreach (var o in origins)
        {
            var delta = (o.TsUtc - ts).Duration();
            if (delta < bestDelta) { bestDelta = delta; best = o; }
        }
        return bestDelta <= MatchWindow ? best : null;
    }

    /// <summary>How far apart a submission and its transcript message may be and still be the same event.</summary>
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionManager.OnSessionCreated -= WireSession;
        _sessionManager.OnSessionRemoved -= UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            UnwireSession(s);
        _handlers.Clear();
        _state.Save();
    }
}

/// <summary>
/// Which messages have already been copied, per SOURCE (see ConversationIngestor.ScopeKey - a
/// transcript file, or a repo for the agents whose history is repo-resolved). Identity is timestamp +
/// role + text hash - NOT a message index, because agents rewrite and compact their transcripts and an
/// index would slide. Persisted so a Director restart cannot re-append a whole history.
/// </summary>
internal sealed class IngestState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _seen = new();

    private static string Path_ => System.IO.Path.Combine(ConversationLog.Directory(), "ingest-state.json");

    private static string Key(DateTime ts, ConversationRole role, string text, bool tsFromAgent)
    {
        // A message with no agent timestamp is stamped with OUR clock, which differs on every read -
        // so its key must not include the time or nothing would ever dedupe. Text + role is what we
        // genuinely have for those agents.
        var timePart = tsFromAgent ? ts.ToString("O") : "no-ts";
        return $"{timePart}|{(int)role}|{text.GetHashCode()}";
    }

    public bool AlreadyWritten(string scope, DateTime ts, ConversationRole role, string text, bool tsFromAgent)
    {
        lock (_gate)
        {
            return _seen.TryGetValue(scope, out var set)
                && set.Contains(Key(ts, role, text, tsFromAgent));
        }
    }

    public void MarkWritten(string scope, DateTime ts, ConversationRole role, string text, bool tsFromAgent)
    {
        lock (_gate)
        {
            if (!_seen.TryGetValue(scope, out var set))
                _seen[scope] = set = new HashSet<string>();
            set.Add(Key(ts, role, text, tsFromAgent));
        }
    }

    public static IngestState Load()
    {
        var state = new IngestState();
        try
        {
            if (!File.Exists(Path_)) return state;
            var json = File.ReadAllText(Path_);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(json);
            if (loaded is not null)
                foreach (var kv in loaded) state._seen[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            // A lost watermark means re-appending, not data loss. Log it and start clean rather than
            // failing the Director over a state file.
            FileLog.Write($"[ConversationIngestor] ingest-state load FAILED, starting clean: {ex.Message}");
        }
        return state;
    }

    public void Save()
    {
        try
        {
            lock (_gate)
            {
                var tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_seen));
                File.Move(tmp, Path_, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConversationIngestor] ingest-state save FAILED (swallowed): {ex.Message}");
        }
    }
}
