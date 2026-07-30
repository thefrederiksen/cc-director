using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Durable store of <see cref="Mission"/> records (see
/// docs/new_architecture/mission-as-first-class-unit-of-work.md). A Mission is a first-class persisted
/// record that sessions attach to, so it MUST survive a Director restart - this store persists the whole
/// set to <c>missions.json</c> in the same director tool-config location as <see cref="SessionStateStore"/>
/// keeps <c>sessions.json</c>. Mirrors that store's JSON options + logging shape, but exposes a record API
/// (Create / Get / List / Delete) because a Mission is an addressable record, not a rebuilt-on-save list.
///
/// Every mutating call reads, mutates, and rewrites the file under a single lock so concurrent Control API
/// requests cannot interleave a half-written set.
///
/// PARTITIONED BY TENANT (devthrottle_internal issue #1039). One hosted Gateway process serves every
/// account from ONE of these files, so a mission carries the tenant that created it and every accessor
/// takes the tenant it is acting for. There is deliberately NO bare-id and NO unscoped accessor left on
/// this type: a caller cannot reach another tenant's record because there is no method that would return
/// one, rather than because each call site remembered to filter. The mission NAME is free text a person
/// typed - customer and project names - so an unpartitioned list is a disclosure of what other accounts
/// are working on, in their own words.
///
/// UNATTRIBUTED ROWS (<see cref="Mission.TenantId"/> null) are records written before missions carried an
/// owner. They cannot be attributed after the fact, so the store never guesses:
///  - Given an <c>adoptUnattributedAs</c> tenant, they are that tenant's. This is for a SINGLE-TENANT
///    install (self-host, the Director), where there is exactly one tenant by construction - so this is a
///    fact about the deployment, not an inference about the row.
///  - Given null (the hosted Gateway, where the file is shared), they belong to NOBODY: they match no
///    tenant, so they are never listed, never resolvable by id, and never deletable through this API.
///    They stay on disk - quarantined, not destroyed - so whoever holds the deployment can still inspect
///    or remove them out of band.
/// </summary>
public sealed class MissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();

    /// <summary>
    /// The tenant that owns rows written before missions carried an owner, or null to quarantine them.
    /// See the type remarks - this is a statement about the DEPLOYMENT (one tenant, or many sharing the
    /// file), decided once at construction where that is known, never inferred per row.
    /// </summary>
    private readonly TenantId? _adoptUnattributedAs;

    public string FilePath { get; }

    /// <param name="filePath">Where the set is persisted. Null uses the Director's tool-config location.</param>
    /// <param name="adoptUnattributedAs">
    /// The owner of unattributed rows. Pass <see cref="TenantId.Local"/> on a single-tenant install; pass
    /// null where more than one tenant shares the file, which quarantines them. It has no default on
    /// purpose: a store that serves several tenants must not inherit a single-tenant answer by omission.
    /// </param>
    public MissionStore(string? filePath, TenantId? adoptUnattributedAs)
    {
        _adoptUnattributedAs = adoptUnattributedAs;
        FilePath = filePath ?? Path.Combine(
            CcStorage.ToolConfig("director"),
            "missions.json");
        FileLog.Write($"[MissionStore] Initialized: FilePath={FilePath} " +
                      $"unattributedRows={(adoptUnattributedAs is { } t ? $"adopted as {t.ToLogString()}" : "quarantined")}");
    }

    /// <summary>
    /// Create and persist a new Mission with a freshly minted id, OWNED BY <paramref name="tenant"/> -
    /// which the caller resolves from its own authenticated identity, never from client input.
    /// <paramref name="missionName"/> is required (a blank name throws) and <paramref name="parentMissionId"/>
    /// nests it under a parent when given. Returns the created record.
    /// </summary>
    public Mission Create(TenantId tenant, string missionName, Guid? parentMissionId = null)
    {
        RequireValid(tenant);
        if (string.IsNullOrWhiteSpace(missionName))
            throw new ArgumentException("missionName is required", nameof(missionName));

        FileLog.Write($"[MissionStore] Create: tenant={tenant.ToLogString()} name=\"{missionName}\" " +
                      $"parent={parentMissionId?.ToString() ?? "(none)"}");

        var mission = new Mission
        {
            MissionId = Guid.NewGuid(),
            MissionName = missionName.Trim(),
            ParentMissionId = parentMissionId,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = tenant.Value,
        };

        lock (_lock)
        {
            var missions = LoadAll();
            missions.Add(mission);
            SaveAll(missions);
        }

        FileLog.Write($"[MissionStore] Create: created mission {mission.MissionId} for tenant {tenant.ToLogString()}");
        return mission;
    }

    /// <summary>
    /// Return <paramref name="tenant"/>'s Mission with the given id, or null when that tenant has no such
    /// mission. A mission another tenant owns is indistinguishable from one that does not exist, which is
    /// the point: the id alone reaches nothing.
    /// </summary>
    public Mission? Get(TenantId tenant, Guid missionId)
    {
        RequireValid(tenant);
        lock (_lock)
            return LoadAll().FirstOrDefault(m => m.MissionId == missionId && OwnedBy(m, tenant));
    }

    /// <summary>Return every Mission <paramref name="tenant"/> owns, oldest first.</summary>
    public IReadOnlyList<Mission> List(TenantId tenant)
    {
        RequireValid(tenant);
        lock (_lock)
            return LoadAll().Where(m => OwnedBy(m, tenant)).OrderBy(m => m.CreatedAt).ToList();
    }

    /// <summary>
    /// Delete <paramref name="tenant"/>'s Mission with the given id. Returns true when a record was
    /// removed - false both when no such mission exists and when it belongs to someone else.
    /// </summary>
    public bool Delete(TenantId tenant, Guid missionId)
    {
        RequireValid(tenant);
        FileLog.Write($"[MissionStore] Delete: tenant={tenant.ToLogString()} mission={missionId}");
        lock (_lock)
        {
            var missions = LoadAll();
            var removed = missions.RemoveAll(m => m.MissionId == missionId && OwnedBy(m, tenant)) > 0;
            if (removed)
                SaveAll(missions);
            return removed;
        }
    }

    /// <summary>
    /// The one ownership test every accessor above shares, so scoping cannot differ between them. A row
    /// with an owner belongs to that owner; a row with none belongs to the adoption tenant, and to nobody
    /// when there is none.
    /// </summary>
    private bool OwnedBy(Mission mission, TenantId tenant)
    {
        var owner = string.IsNullOrWhiteSpace(mission.TenantId)
            ? _adoptUnattributedAs?.Value
            : mission.TenantId;
        return owner is not null && string.Equals(owner, tenant.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refuse a <c>default(TenantId)</c>. Every accessor is scoped by comparison against the caller's
    /// tenant, so an invalid tenant must fail loudly here rather than compare its null Value against a
    /// row and quietly decide something.
    /// </summary>
    private static void RequireValid(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A MissionStore call needs a valid tenant.", nameof(tenant));
    }

    /// <summary>Read the full set from disk. An absent file is an empty set; a corrupt file fails loudly.</summary>
    private List<Mission> LoadAll()
    {
        if (!File.Exists(FilePath))
            return new List<Mission>();

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<Mission>>(json, JsonOptions) ?? new List<Mission>();
    }

    /// <summary>Write the full set to disk, creating the directory when missing.</summary>
    private void SaveAll(List<Mission> missions)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException($"Cannot determine directory from path: {FilePath}");

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            FileLog.Write($"[MissionStore] SaveAll: created directory {dir}");
        }

        var serialized = JsonSerializer.Serialize(missions, JsonOptions);
        File.WriteAllText(FilePath, serialized);
        FileLog.Write($"[MissionStore] SaveAll: saved {missions.Count} mission(s)");
    }
}
