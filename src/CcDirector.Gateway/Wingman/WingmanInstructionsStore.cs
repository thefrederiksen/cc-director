using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The editable, versioned store of the wingman's instructions (issue #537). The wingman uses the
/// ACTIVE instructions: the user's active custom version when set, otherwise the DEPLOYED DEFAULT
/// (<see cref="WingmanTranslator.FidelityPrompt"/>, shipped by the DevThrottle dev team and carrying
/// <see cref="WingmanTranslator.DefaultInstructionsVersion"/>).
///
/// Managed-default behavior:
/// - A user with NO customization always tracks the latest deployed default automatically.
/// - When the user HAS customized and a new release ships a CHANGED default, <see cref="UpdateAvailable"/>
///   turns true and the page can show the diff of the dev team's changes (the acknowledged default
///   content vs the new default) and offer a one-click switch - never silently overwriting the user's
///   prompt. Acknowledging (switch, or customizing afresh) snapshots the current default.
///
/// PERSISTENCE (Hosted Gateway mission, Step 1b): the whole state document lives in the EF data layer's
/// <c>wingman_instructions</c> table as ONE row per tenant (the active-version pointer, the acknowledged
/// deployed-default snapshot, and the versions as an owned JSON collection) - NOT the old hand-rolled
/// <c>wingman-instructions.json</c>. The public API and observable behavior are unchanged. The store keeps
/// the working state in memory exactly as before; only the load/save primitive moved from a JSON file to the
/// single row. On first run after the upgrade a legacy <c>wingman-instructions.json</c> is imported once
/// (through the shared recoverable-import helper) then renamed aside. Thread-safe; fail-loud on a persist
/// error (no silent best-effort fallback, matching the other migrated stores).
/// </summary>
public sealed class WingmanInstructionsStore
{
    /// <summary>One saved version of the instructions.</summary>
    public sealed class InstructionVersion
    {
        public string Id { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public string? Label { get; set; }
        public string Source { get; set; } = "user";   // "user" | "default"
        public string Hash { get; set; } = "";
    }

    private sealed class StateFile
    {
        public string? ActiveVersionId { get; set; }
        public string AckDefaultVersion { get; set; } = "";
        public string AckDefaultContent { get; set; } = "";
        public List<InstructionVersion> Versions { get; set; } = new();
    }

    /// <summary>Hard cap so a pasted prompt cannot bloat the brain call / file.</summary>
    public const int MaxContentChars = 20_000;

    private readonly GatewayDatabase _db;
    private readonly string _legacyJsonPath;
    private readonly string _defaultContent;
    private readonly string _defaultVersion;
    private readonly object _lock = new();
    private StateFile _state = new();
    private int _seq;

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <param name="legacyJsonPath">The legacy <c>wingman-instructions.json</c> path to import ONCE if it
    /// exists and the table is empty. REQUIRED (no silent default).</param>
    /// <param name="deferInitialize">
    /// When true the constructor validates arguments and stops; the caller must call
    /// <see cref="Initialize"/> once the database is open. The Gateway passes true so its listener can bind
    /// BEFORE any database work - the load below used to sit in front of the bind, and a slow database
    /// therefore delayed it past the platform's container-start deadline (#2383, #2585).
    ///
    /// The caller MUST run Initialize inside the same ambient tenant scope the constructor would have had.
    /// Nothing is served in the meantime: the readiness gate refuses every request but /healthz.
    /// </param>
    public WingmanInstructionsStore(GatewayDatabase db, string legacyJsonPath,
        string? defaultContent = null, string? defaultVersion = null, bool deferInitialize = false)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrWhiteSpace(legacyJsonPath))
            throw new ArgumentException("legacy json path is required", nameof(legacyJsonPath));
        _legacyJsonPath = legacyJsonPath;
        _defaultContent = defaultContent ?? WingmanTranslator.FidelityPrompt;
        _defaultVersion = defaultVersion ?? WingmanTranslator.DefaultInstructionsVersion;
        if (!deferInitialize)
            InitializeCore();
    }

    /// <summary>
    /// Run the deferred load. Idempotent, and a no-op for an instance whose constructor already did it.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        InitializeCore();
    }

    /// <summary>True once the load has run.</summary>
    public bool IsInitialized => _initialized;

    private bool _initialized;

    private void InitializeCore()
    {
        Load();
        _initialized = true;
    }


    public string DefaultContent => _defaultContent;
    public string DefaultVersion => _defaultVersion;
    public string DefaultHash => Hash(_defaultContent);

    /// <summary>Short stable content fingerprint (the real identity of a set of instructions).</summary>
    public static string Hash(string? s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private void Load()
    {
        lock (_lock)
        {
            // One-time legacy import (fail-loud, all-or-nothing), then read the single persisted row into the
            // in-memory working state. A missing row is a fresh store (new state).
            ImportLegacyJsonIfNeeded();
            _state = ReadStateFromDb() ?? new StateFile();

            // First run, or a user who has never customized: they ride the latest deployed default,
            // so acknowledge it (no stale "update available" banner).
            if (!IsCustomizedNoLock() && Hash(_state.AckDefaultContent) != DefaultHash)
                AcknowledgeDefaultNoLock();
            else if (string.IsNullOrEmpty(_state.AckDefaultContent))
                AcknowledgeDefaultNoLock();
        }
    }

    /// <summary>Persist the in-memory state to the single per-tenant row. Fail-loud: a persist error
    /// propagates rather than silently dropping the change.</summary>
    private void Save()
    {
        using var ctx = _db.CreateContext();
        var row = ctx.WingmanInstructions.FirstOrDefault();
        if (row is null)
        {
            row = new WingmanInstructionEntity { TenantId = ctx.ActiveTenant! };
            ctx.WingmanInstructions.Add(row);
        }
        row.ActiveVersionId = _state.ActiveVersionId;
        row.AckDefaultVersion = _state.AckDefaultVersion;
        row.AckDefaultContent = _state.AckDefaultContent;
        row.Versions = _state.Versions.Select(ToOwned).ToList();
        ctx.SaveChanges();
    }

    /// <summary>Read the single per-tenant state row into an in-memory <see cref="StateFile"/>, or null when
    /// no row exists yet.</summary>
    private StateFile? ReadStateFromDb()
    {
        using var ctx = _db.CreateContext();
        var row = ctx.WingmanInstructions.AsNoTracking().FirstOrDefault();
        if (row is null)
            return null;
        return new StateFile
        {
            ActiveVersionId = row.ActiveVersionId,
            AckDefaultVersion = row.AckDefaultVersion,
            AckDefaultContent = row.AckDefaultContent,
            Versions = row.Versions.Select(ToPublic).ToList(),
        };
    }

    private static InstructionVersion ToPublic(WingmanInstructionVersionOwned o) => new()
    {
        Id = o.Id,
        Content = o.Content,
        CreatedAtUtc = o.CreatedAtUtc,
        Label = o.Label,
        Source = o.Source,
        Hash = o.Hash,
    };

    private static WingmanInstructionVersionOwned ToOwned(InstructionVersion v) => new()
    {
        Id = v.Id,
        Content = v.Content,
        CreatedAtUtc = v.CreatedAtUtc,
        Label = v.Label,
        Source = v.Source,
        Hash = v.Hash,
    };

    private void AcknowledgeDefaultNoLock()
    {
        _state.AckDefaultVersion = _defaultVersion;
        _state.AckDefaultContent = _defaultContent;
        Save();
    }

    private bool IsCustomizedNoLock()
        => _state.ActiveVersionId is not null && FindNoLock(_state.ActiveVersionId) is not null;

    private InstructionVersion? FindNoLock(string? id)
        => id is null ? null : _state.Versions.FirstOrDefault(v => v.Id == id);

    private string NextId()
    {
        // Monotonic per-process plus a content-independent suffix; Date.Now is fine here (not a workflow).
        _seq++;
        return $"v{DateTime.UtcNow:yyyyMMddHHmmss}-{_seq}";
    }

    /// <summary>True when the user has an active custom version (vs. riding the deployed default).</summary>
    public bool IsCustomized { get { lock (_lock) return IsCustomizedNoLock(); } }

    /// <summary>The instructions the wingman uses right now: the active custom version, else the default.</summary>
    public string ActiveContent
    {
        get
        {
            lock (_lock)
            {
                if (_state.ActiveVersionId is not null && FindNoLock(_state.ActiveVersionId) is { } v) return v.Content;
                return _defaultContent;
            }
        }
    }

    /// <summary>The deployed default the user has customized has been superseded by a newer dev-team
    /// default. Only true while customized - a non-customized user always rides the latest default.</summary>
    public bool UpdateAvailable
    {
        get { lock (_lock) return IsCustomizedNoLock() && Hash(_state.AckDefaultContent) != DefaultHash; }
    }

    /// <summary>The active version (a custom one, or a synthesized record for the deployed default).</summary>
    public InstructionVersion Active()
    {
        lock (_lock)
        {
            if (_state.ActiveVersionId is not null && FindNoLock(_state.ActiveVersionId) is { } v) return v;
            return DefaultAsVersionNoLock();
        }
    }

    private InstructionVersion DefaultAsVersionNoLock() => new()
    {
        Id = "default",
        Content = _defaultContent,
        CreatedAtUtc = DateTime.UtcNow,
        Label = $"DevThrottle default (v{_defaultVersion})",
        Source = "default",
        Hash = DefaultHash,
    };

    /// <summary>Deployed default as a version record (for display / diff against a custom version).</summary>
    public InstructionVersion DefaultAsVersion() { lock (_lock) return DefaultAsVersionNoLock(); }

    /// <summary>The acknowledged (based-on) default content - the left side of the "our changes" diff.</summary>
    public (string version, string content) AcknowledgedDefault()
    {
        lock (_lock) return (_state.AckDefaultVersion, _state.AckDefaultContent);
    }

    /// <summary>Version history, newest first.</summary>
    public IReadOnlyList<InstructionVersion> Versions()
    {
        lock (_lock) return _state.Versions.OrderByDescending(v => v.CreatedAtUtc).ToList();
    }

    public InstructionVersion? Get(string id)
    {
        lock (_lock) return FindNoLock(id);
    }

    /// <summary>Save a new custom version from edited content and make it the active instructions.
    /// Acknowledges the current default (the user is editing against it). Throws on empty/oversized.</summary>
    public InstructionVersion Save(string content, string? label)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Instructions content is required.", nameof(content));
        if (content.Length > MaxContentChars)
            throw new ArgumentException($"Instructions exceed the {MaxContentChars}-character limit.", nameof(content));

        lock (_lock)
        {
            var v = new InstructionVersion
            {
                Id = NextId(),
                Content = content,
                CreatedAtUtc = DateTime.UtcNow,
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                Source = "user",
                Hash = Hash(content),
            };
            _state.Versions.Add(v);
            _state.ActiveVersionId = v.Id;
            // Editing is done against the current default; acknowledge it so the banner only fires on a
            // LATER dev-team change.
            _state.AckDefaultVersion = _defaultVersion;
            _state.AckDefaultContent = _defaultContent;
            Save();
            FileLog.Write($"[WingmanInstructionsStore] saved version {v.Id} (len={content.Length}, hash={v.Hash})");
            return v;
        }
    }

    /// <summary>Make an existing version active again. Returns false if the id is unknown.</summary>
    public bool Revert(string id)
    {
        lock (_lock)
        {
            if (FindNoLock(id) is null) return false;
            _state.ActiveVersionId = id;
            Save();
            FileLog.Write($"[WingmanInstructionsStore] reverted active to {id}");
            return true;
        }
    }

    /// <summary>Adopt the deployed default: drop the active custom version and acknowledge the current
    /// default (clears <see cref="UpdateAvailable"/>).</summary>
    public void SwitchToDefault()
    {
        lock (_lock)
        {
            _state.ActiveVersionId = null;
            AcknowledgeDefaultNoLock();
            FileLog.Write($"[WingmanInstructionsStore] switched to deployed default v{_defaultVersion}");
        }
    }

    // ---- one-time legacy JSON import --------------------------------------------------------------

    /// <summary>
    /// Import a legacy <c>wingman-instructions.json</c> exactly once, through the shared recoverable-import
    /// plumbing (<see cref="LegacyJsonImport.Recoverable"/>): import only when the file exists AND the table
    /// is empty; recover a lingering file idempotently; rename aside best-effort after a successful import.
    /// </summary>
    private void ImportLegacyJsonIfNeeded()
        => LegacyJsonImport.Recoverable(
            _legacyJsonPath,
            "[WingmanInstructionsStore]",
            isPopulated: () => { using var ctx = _db.CreateContext(); return ctx.WingmanInstructions.Any(); },
            importCommitted: ImportRowFromLegacyJson);

    /// <summary>
    /// Parse the legacy state file and insert it as the single per-tenant row - the active-version pointer,
    /// the acknowledged default snapshot, and the versions (order preserved). Fail-loud and all-or-nothing -
    /// a parse error (or a null document) throws and imports nothing (the file is left in place).
    /// </summary>
    private void ImportRowFromLegacyJson()
    {
        StateFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_legacyJsonPath));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanInstructionsStore] Import FAILED: legacy file {_legacyJsonPath} could not be read: {ex.Message}");
            throw new InvalidOperationException(
                $"The legacy wingman-instructions file '{_legacyJsonPath}' could not be parsed for the one-time " +
                $"import: {ex.Message}. The Gateway will not start with a partial import. Fix or move the file " +
                "aside and restart.", ex);
        }

        if (parsed is null)
        {
            FileLog.Write($"[WingmanInstructionsStore] Import FAILED: legacy file {_legacyJsonPath} deserialized to a null document");
            throw new InvalidOperationException(
                $"The legacy wingman-instructions file '{_legacyJsonPath}' could not be parsed for the one-time " +
                "import: the document is null. The Gateway will not start with a partial import. Fix or move the " +
                "file aside and restart.");
        }

        using var ctx = _db.CreateContext();
        ctx.WingmanInstructions.Add(new WingmanInstructionEntity
        {
            TenantId = ctx.ActiveTenant!,
            ActiveVersionId = parsed.ActiveVersionId,
            AckDefaultVersion = parsed.AckDefaultVersion,
            AckDefaultContent = parsed.AckDefaultContent,
            Versions = (parsed.Versions ?? new List<InstructionVersion>()).Select(ToOwned).ToList(),
        });
        ctx.SaveChanges();

        FileLog.Write($"[WingmanInstructionsStore] Import: state ({parsed.Versions?.Count ?? 0} version(s)) imported from {_legacyJsonPath}");
    }
}
