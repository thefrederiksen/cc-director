using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway;

/// <summary>
/// The fleet's named work-list store (issue #273, child of #270; persistent since #301). A named work list
/// is an ordered list of structured item refs (<see cref="WorkListItemRef"/>) plus a single-consumer claim.
///
/// PERSISTENCE (Hosted Gateway mission, Step 1b): lists live in the EF data layer's <c>worklists</c> table
/// with their items in the ordered <c>worklist_items</c> child table (SQLite locally), NOT the old
/// hand-rolled <c>worklists.json</c>. The public API and observable behavior are unchanged.
///
/// CASE-INSENSITIVE NAMES, PROVIDER-AWARE: names address one list case-insensitively ("Backlog" and
/// "backlog" are the same list), exactly as the old OrdinalIgnoreCase dictionary. This is enforced in the
/// database by a NOCASE collation on the name column plus a unique per-tenant index (see
/// <see cref="Data.GatewayDbContext"/>), NOT by EF.Functions.ILike (Postgres-only). The Postgres provider
/// selects its own case-insensitive collation later; the store code is provider-agnostic.
///
/// STALE-CLAIM RELEASE: a persisted consumer claim is by definition stale after a restart (the claiming
/// runner died with the Gateway), so every persisted claim is released on construction - preserved exactly.
///
/// ONE-TIME IMPORT: on first run after the upgrade, if a legacy <c>worklists.json</c> exists and the table
/// is empty, every list (name preserving case, items preserving order, and the consumer/claim) is imported
/// inside one transaction, then the JSON is renamed aside as a backup. Fail-loud and all-or-nothing, the
/// cron-store contract. The stale-claim release runs AFTER the import, exactly as a real restart would, so a
/// claim imported losslessly is then released (the claim does not survive a restart).
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over a
/// fresh pooled context.
/// </summary>
public sealed class WorkListStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly string _legacyJsonPath;

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The number of stale consumer claims released by the most recent load (import + stale-release on
    /// construction). A test-observability seam that surfaces the count Load already computes: it lets a test
    /// prove the import carried a claim into the database (count 1) before the load-time release cleared it
    /// (Consumer null) - it is NOT a behavior change.
    /// </summary>
    internal int LastLoadStaleClaimsReleased { get; private set; }

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <param name="legacyJsonPath">The legacy <c>worklists.json</c> path to import ONCE if it exists and the
    /// table is empty. REQUIRED (no silent default).</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <exception cref="ArgumentException">The legacy path is null/empty/whitespace.</exception>
    public WorkListStore(GatewayDatabase db, string legacyJsonPath)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrWhiteSpace(legacyJsonPath))
            throw new ArgumentException("legacy json path is required", nameof(legacyJsonPath));
        _legacyJsonPath = legacyJsonPath;

        lock (_gate)
        {
            ImportLegacyJsonIfNeeded();
            ReleaseStaleClaimsOnLoad();
        }
    }

    /// <summary>The outcome of a single-consumer claim attempt.</summary>
    public enum ClaimResult
    {
        /// <summary>The claim was granted; the token is the active consumer.</summary>
        Granted,

        /// <summary>The list is already claimed by another consumer; the claim is refused.</summary>
        AlreadyClaimed,

        /// <summary>No list with that name exists.</summary>
        NoSuchList,
    }

    /// <summary>
    /// Create a named list. Returns false if a list with that name already exists (case-insensitively); the
    /// existing list is left untouched.
    /// </summary>
    /// <exception cref="ArgumentException">The name is null/empty/whitespace.</exception>
    public bool Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("list name is required", nameof(name));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            // Case-insensitive collision matched in CODE via OrdinalIgnoreCase (loading the tenant's lists),
            // because no SQL comparison reproduces OrdinalIgnoreCase exactly. Race-free under the write lock,
            // exactly like the old Dictionary(OrdinalIgnoreCase).
            if (FindList(ctx, name) is not null)
            {
                FileLog.Write($"[WorkListStore] Create: name={name} already exists");
                return false;
            }

            ctx.WorkLists.Add(new WorkListEntity { Id = Guid.NewGuid(), TenantId = ctx.ActiveTenant!, Name = name });
            ctx.SaveChanges();
            FileLog.Write($"[WorkListStore] Create: name={name}");
            return true;
        }
    }

    /// <summary>All named lists, name-sorted (OrdinalIgnoreCase). Each is a defensive copy.</summary>
    public IReadOnlyList<WorkListDto> ListAll()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var lists = ctx.WorkLists.AsNoTracking().ToList();
            var itemsByList = ctx.WorkListItems.AsNoTracking()
                .ToList()
                .GroupBy(i => i.WorkListId)
                .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Position).ToList());

            return lists
                .Select(l => ToDto(l, itemsByList.TryGetValue(l.Id, out var items) ? items : new List<WorkListItemEntity>()))
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>One list by name (case-insensitive) as a defensive copy, or null if absent.</summary>
    public WorkListDto? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var list = FindList(ctx, name);
            if (list is null) return null;
            var items = ctx.WorkListItems.AsNoTracking()
                .Where(i => i.WorkListId == list.Id)
                .OrderBy(i => i.Position)
                .ToList();
            return ToDto(list, items);
        }
    }

    /// <summary>
    /// Append one structured item ref to the end of the named list. Returns false if no list with that name
    /// exists. Any source is stored (mixed sources coexist in one ordered list).
    /// </summary>
    /// <exception cref="ArgumentNullException">The ref is null.</exception>
    /// <exception cref="ArgumentException">The ref's source or id is null/empty/whitespace.</exception>
    public bool AppendItem(string name, WorkListItemRef item)
    {
        if (item is null)
            throw new ArgumentNullException(nameof(item));
        if (string.IsNullOrWhiteSpace(item.Source))
            throw new ArgumentException("item source is required", nameof(item));
        if (string.IsNullOrWhiteSpace(item.Id))
            throw new ArgumentException("item id is required", nameof(item));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var list = FindList(ctx, name);
            if (list is null)
            {
                FileLog.Write($"[WorkListStore] AppendItem: no such list name={name}");
                return false;
            }

            var nextPos = (ctx.WorkListItems.Where(i => i.WorkListId == list.Id).Max(i => (int?)i.Position) ?? -1) + 1;
            ctx.WorkListItems.Add(NewItem(list, ctx.ActiveTenant!, nextPos, item));
            ctx.SaveChanges();
            var count = ctx.WorkListItems.Count(i => i.WorkListId == list.Id);
            FileLog.Write($"[WorkListStore] AppendItem: name={name}, source={item.Source}, id={item.Id}, count={count}");
            return true;
        }
    }

    /// <summary>
    /// Replace the named list's items with the supplied ordered array (reorder). Returns false if no list
    /// with that name exists. The new array is taken verbatim as the new order.
    /// </summary>
    /// <exception cref="ArgumentNullException">The items array is null.</exception>
    /// <exception cref="ArgumentException">Any ref has a null/empty source or id.</exception>
    public bool Reorder(string name, IReadOnlyList<WorkListItemRef> items)
    {
        if (items is null)
            throw new ArgumentNullException(nameof(items));
        foreach (var item in items)
        {
            if (item is null)
                throw new ArgumentException("an item ref in the array is null", nameof(items));
            if (string.IsNullOrWhiteSpace(item.Source))
                throw new ArgumentException("an item ref is missing its source", nameof(items));
            if (string.IsNullOrWhiteSpace(item.Id))
                throw new ArgumentException("an item ref is missing its id", nameof(items));
        }

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var list = FindList(ctx, name);
            if (list is null)
            {
                FileLog.Write($"[WorkListStore] Reorder: no such list name={name}");
                return false;
            }

            // Replace the whole ordered set in ONE change set (delete existing rows + insert the new order).
            var existing = ctx.WorkListItems.Where(i => i.WorkListId == list.Id).ToList();
            ctx.WorkListItems.RemoveRange(existing);
            for (var pos = 0; pos < items.Count; pos++)
                ctx.WorkListItems.Add(NewItem(list, ctx.ActiveTenant!, pos, items[pos]));
            ctx.SaveChanges();
            FileLog.Write($"[WorkListStore] Reorder: name={name}, count={items.Count}");
            return true;
        }
    }

    /// <summary>
    /// Remove the item(s) addressed by source + id (case-insensitive on source, exact on id) from the named
    /// list, preserving the relative order of the rest. Returns true if an item was removed, false if no list
    /// with that name exists or no item matched.
    /// </summary>
    public bool RemoveItem(string name, string source, string id)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(id))
            return false;

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var list = FindList(ctx, name);
            if (list is null)
            {
                FileLog.Write($"[WorkListStore] RemoveItem: no such list name={name}");
                return false;
            }

            // Match in memory with the exact .NET semantics the JSON store used: source OrdinalIgnoreCase,
            // id Ordinal. The remaining items keep their positions, so ordering is preserved on read.
            var doomed = ctx.WorkListItems
                .Where(i => i.WorkListId == list.Id)
                .ToList()
                .Where(i => string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(i.ItemId, id, StringComparison.Ordinal))
                .ToList();

            if (doomed.Count > 0)
            {
                ctx.WorkListItems.RemoveRange(doomed);
                ctx.SaveChanges();
            }
            FileLog.Write($"[WorkListStore] RemoveItem: name={name}, source={source}, id={id}, removed={doomed.Count}");
            return doomed.Count > 0;
        }
    }

    /// <summary>
    /// Claim the single draining consumer for the named list. Granted only when currently unclaimed; a claim
    /// on an already-claimed list is refused. The supplied token becomes the active consumer on success.
    /// </summary>
    /// <exception cref="ArgumentException">The token is null/empty/whitespace.</exception>
    public ClaimResult Claim(string name, string consumerToken)
    {
        if (string.IsNullOrWhiteSpace(consumerToken))
            throw new ArgumentException("consumer token is required", nameof(consumerToken));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var list = FindList(ctx, name);
            if (list is null)
            {
                FileLog.Write($"[WorkListStore] Claim: no such list name={name}");
                return ClaimResult.NoSuchList;
            }

            if (!string.IsNullOrEmpty(list.Consumer))
            {
                FileLog.Write($"[WorkListStore] Claim: name={name} already claimed");
                return ClaimResult.AlreadyClaimed;
            }

            list.Consumer = consumerToken;
            ctx.SaveChanges();
            FileLog.Write($"[WorkListStore] Claim: name={name} granted");
            return ClaimResult.Granted;
        }
    }

    /// <summary>
    /// Release the consumer claim on the named list. Returns false if no list with that name exists;
    /// releasing an already-unclaimed list is a no-op that returns true (the post-condition holds).
    /// </summary>
    public bool Release(string name)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var list = FindList(ctx, name);
            if (list is null)
            {
                FileLog.Write($"[WorkListStore] Release: no such list name={name}");
                return false;
            }

            if (list.Consumer is not null)
            {
                list.Consumer = null;
                ctx.SaveChanges();
            }
            FileLog.Write($"[WorkListStore] Release: name={name}");
            return true;
        }
    }

    /// <summary>
    /// Release every persisted consumer claim on construction: a persisted claim's runner died with the
    /// Gateway, so it is stale by definition (issue #301 policy). Reproduces the JSON store's load-time
    /// release exactly. Records the count in <see cref="LastLoadStaleClaimsReleased"/>.
    /// </summary>
    private void ReleaseStaleClaimsOnLoad()
    {
        using var ctx = _db.CreateContext();
        var claimed = ctx.WorkLists.Where(e => e.Consumer != null).ToList();
        var released = 0;
        foreach (var list in claimed)
        {
            if (string.IsNullOrEmpty(list.Consumer)) continue;
            FileLog.Write($"[WorkListStore] Load: released stale claim on list={list.Name}, deadConsumer={list.Consumer} (claims do not survive a Gateway restart)");
            list.Consumer = null;
            released++;
        }
        if (released > 0)
            ctx.SaveChanges();

        LastLoadStaleClaimsReleased = released;
        FileLog.Write($"[WorkListStore] Load: {ctx.WorkLists.Count()} list(s) present, staleClaimsReleased={released}");
    }

    /// <summary>
    /// Find one list by name, case-insensitively via <see cref="StringComparer.OrdinalIgnoreCase"/> matched in
    /// CODE (loading the tenant's lists - the query is tenant-scoped by the global filter), because no SQL
    /// comparison reproduces OrdinalIgnoreCase exactly. This is what makes the name key EXACTLY the legacy
    /// Dictionary(OrdinalIgnoreCase) - full Unicode, no U+017F over-merge, no brick. The returned entity is
    /// tracked, so a caller can mutate and save it. Null on a null/blank name or no match. Small N (a fleet
    /// has few named lists), the same cost as the old store's whole-file load.
    /// </summary>
    private static WorkListEntity? FindList(GatewayDbContext ctx, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return ctx.WorkLists.AsEnumerable().FirstOrDefault(e => StringComparer.OrdinalIgnoreCase.Equals(e.Name, name));
    }

    private static WorkListItemEntity NewItem(WorkListEntity list, string tenantId, int position, WorkListItemRef item) => new()
    {
        Id = Guid.NewGuid(),
        WorkListId = list.Id,
        TenantId = tenantId,
        Position = position,
        Source = item.Source,
        ItemId = item.Id,
        Area = item.Area,
    };

    private static WorkListDto ToDto(WorkListEntity list, List<WorkListItemEntity> items) => new()
    {
        Name = list.Name,
        Consumer = list.Consumer,
        Items = items
            .OrderBy(i => i.Position)
            .Select(i => new WorkListItemRef { Source = i.Source, Id = i.ItemId, Area = i.Area })
            .ToList(),
    };

    // ---- one-time legacy JSON import --------------------------------------------------------------

    /// <summary>The on-disk shape of the legacy store file: one document holding every named list.</summary>
    private sealed class StoreFile
    {
        public List<WorkListDto> Lists { get; set; } = new();
    }

    /// <summary>
    /// Import a legacy <c>worklists.json</c> exactly once: only when it exists AND the table is empty. Every
    /// list's name (case preserved), items (ORDER preserved), and consumer/claim (preserved losslessly) are
    /// inserted inside one transaction, then the JSON is renamed aside. Fail-loud and all-or-nothing - a parse
    /// error throws and imports nothing (the file is left in place). The load-time stale-claim release runs
    /// AFTER this, so an imported claim is then released exactly as a restart would release it.
    /// </summary>
    private void ImportLegacyJsonIfNeeded()
    {
        if (!File.Exists(_legacyJsonPath))
            return;

        using var ctx = _db.CreateContext();
        if (ctx.WorkLists.Any())
        {
            // Recovery (idempotent): a prior import committed but its rename-aside did not complete (the file
            // was briefly locked), so the legacy file lingers even though the data is already in the database.
            // Rename it aside now - NEVER re-importing over existing rows - so the leftover file is cleaned up
            // and can never be re-imported. Best-effort: if the rename fails again the data is safe and the
            // next boot retries.
            TryRenameAside();
            return;
        }

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_legacyJsonPath), FileJsonOptions);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkListStore] Import FAILED: legacy file {_legacyJsonPath} could not be read: {ex.Message}");
            throw new InvalidOperationException(
                $"The legacy work-lists file '{_legacyJsonPath}' could not be parsed for the one-time import: " +
                $"{ex.Message}. The Gateway will not start with a partial import. Fix or move the file aside " +
                "and restart.", ex);
        }

        var lists = parsed?.Lists ?? new List<WorkListDto>();

        using var tx = ctx.Database.BeginTransaction();
        foreach (var list in lists)
        {
            if (string.IsNullOrWhiteSpace(list.Name))
                throw new InvalidOperationException(
                    $"The legacy work-lists file '{_legacyJsonPath}' has a list with an empty name; refusing a " +
                    "partial import.");

            var entity = new WorkListEntity
            {
                Id = Guid.NewGuid(),
                TenantId = ctx.ActiveTenant!,
                Name = list.Name,
                Consumer = list.Consumer,
            };
            ctx.WorkLists.Add(entity);
            for (var pos = 0; pos < list.Items.Count; pos++)
                ctx.WorkListItems.Add(NewItem(entity, ctx.ActiveTenant!, pos, list.Items[pos]));
        }
        ctx.SaveChanges();
        tx.Commit();

        TryRenameAside();
        FileLog.Write($"[WorkListStore] Import: {lists.Count} list(s) imported from {_legacyJsonPath}");
    }

    /// <summary>
    /// Rename the imported legacy file aside, best-effort. The data is already committed to the database and
    /// the empty-table guard prevents any re-import, so a failed rename (e.g. the file is briefly locked) must
    /// NOT fail the Gateway - it is logged and left for the next boot's recovery to retry.
    /// </summary>
    private void TryRenameAside()
    {
        try
        {
            LegacyJsonImport.RenameAside(_legacyJsonPath, "[WorkListStore]");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkListStore] Import: rename-aside of {_legacyJsonPath} failed (data is safe in the " +
                          $"database; the next boot retries): {ex.Message}");
        }
    }
}
