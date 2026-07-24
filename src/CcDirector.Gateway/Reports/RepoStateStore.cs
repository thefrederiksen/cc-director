using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Reports;

/// <summary>Thrown when a repo-state push is malformed. The endpoint maps it to a 400 with the message,
/// so a Director learns what it sent wrong instead of having its snapshot silently dropped.</summary>
public sealed class RepoStateValidationException : Exception
{
    public RepoStateValidationException(string message) : base(message) { }
}

/// <summary>
/// The Gateway's repo-state store (issue #2118) over the <c>repo_state</c> table: the LATEST branches and
/// worktrees each Director reports for each repository, which is the one hygiene feed the morning report
/// cannot assemble from Gateway-side stores.
///
/// OVERWRITE, NOT APPEND: one row per (tenant, director, repository), replaced on every push. The report
/// asks what a repository looks like NOW, so keeping a history would buy storage and retention work for a
/// question nobody asks.
///
/// EVERY WRITE IS SCOPED BY THE PUSHER'S OWN TENANT, and the tenant comes from the caller's authenticated
/// device key rather than from anything in the payload - so a body cannot claim to be another account's
/// machine. The read is scoped the same way: the report reads only the tenant it was asked about.
///
/// Threading matches the rest of the data layer: single writer, write lock, fresh pooled context per
/// operation.
/// </summary>
public sealed class RepoStateStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The hard ceiling on one push, so a single request cannot ask the Gateway to materialize an
    /// unbounded write set. A machine with more repositories than this pushes the first
    /// <see cref="MaxRepositoriesPerPush"/> and the overflow is REJECTED loudly rather than truncated.</summary>
    public const int MaxRepositoriesPerPush = 200;

    /// <summary>Identity fields - ids and paths, bounded because they are names, not prose.</summary>
    public const int MaxIdChars = 512;

    /// <summary>The hard ceiling on one serialized branch or worktree list. A repository with more branches
    /// than this serializes to a payload no report needs; the push is rejected rather than truncated.</summary>
    public const int MaxListJsonChars = 200_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public RepoStateStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Store a Director's batch, replacing the row for each (tenant, director, repository) it names.
    /// Validated as a WHOLE first: one malformed repository rejects the entire push, because a half-landed
    /// batch would leave the report reading a mixture of this snapshot and the last one and calling it a
    /// single moment in time. Returns the number of repositories stored.
    /// </summary>
    /// <exception cref="RepoStateValidationException">The push or a repository in it is malformed.</exception>
    public int StoreBatch(TenantId tenant, string directorId, string machineName,
        IReadOnlyList<RepoStateSnapshotDto> repositories, DateTime receivedAtUtc)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        if (repositories is null)
            throw new RepoStateValidationException("A repositories list is required.");

        var director = Required(directorId, "directorId");
        if (repositories.Count > MaxRepositoriesPerPush)
            throw new RepoStateValidationException(
                $"A push carries at most {MaxRepositoriesPerPush} repositories (got {repositories.Count}).");
        if (repositories.Count == 0)
            return 0;

        var received = DateTime.SpecifyKind(receivedAtUtc.ToUniversalTime(), DateTimeKind.Utc);
        var machine = string.IsNullOrWhiteSpace(machineName) ? "" : machineName.Trim();
        if (machine.Length > MaxIdChars)
            throw new RepoStateValidationException($"The machineName is too long (limit {MaxIdChars} characters).");

        // Build (and therefore validate) every row before touching the database.
        var rows = new List<RepoStateEntity>(repositories.Count);
        foreach (var repo in repositories)
        {
            if (repo is null)
                throw new RepoStateValidationException("The repositories list contains a null entry.");
            rows.Add(BuildRow(tenant, director, machine, repo, received));
        }

        // A duplicate repository path INSIDE one push is a producer bug, not something to silently collapse:
        // two rows with the same key would make the stored answer depend on iteration order.
        var duplicate = rows.GroupBy(r => r.RepoPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new RepoStateValidationException(
                $"The push names the same repository path more than once ('{duplicate.Key}').");

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);

            var paths = rows.Select(r => r.RepoPath).ToList();
            // Reads THROUGH the global tenant query filter, so this only ever finds this tenant's own rows -
            // another account's row for the same path is invisible here and is never overwritten.
            var existing = ctx.RepoState
                .Where(e => e.DirectorId == director && paths.Contains(e.RepoPath))
                .ToDictionary(e => e.RepoPath, StringComparer.Ordinal);

            foreach (var row in rows)
            {
                if (existing.TryGetValue(row.RepoPath, out var current))
                {
                    current.MachineName = row.MachineName;
                    current.Name = row.Name;
                    current.DefaultBranch = row.DefaultBranch;
                    current.CurrentBranch = row.CurrentBranch;
                    current.IsDirty = row.IsDirty;
                    current.CollectedAtUtc = row.CollectedAtUtc;
                    current.ReceivedAtUtc = row.ReceivedAtUtc;
                    current.BranchesJson = row.BranchesJson;
                    current.WorktreesJson = row.WorktreesJson;
                }
                else
                {
                    ctx.RepoState.Add(row);
                }
            }

            ctx.SaveChanges();
        }

        FileLog.Write($"[RepoStateStore] StoreBatch: tenant={tenant.ToLogString()} director={director} stored {rows.Count} repositories");
        return rows.Count;
    }

    /// <summary>
    /// Every repository snapshot this tenant holds, newest first. Rows whose <c>ReceivedAtUtc</c> is older
    /// than <paramref name="maxAge"/> are EXCLUDED: a Director that stopped pushing a week ago knows nothing
    /// about that repository today, and serving its last snapshot would let the report recommend deleting a
    /// worktree the owner has since started working in.
    /// </summary>
    public IReadOnlyList<StoredRepoState> ReadFresh(TenantId tenant, TimeSpan maxAge, DateTime nowUtc)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));

        var cutoff = DateTime.SpecifyKind(nowUtc.ToUniversalTime(), DateTimeKind.Utc) - maxAge;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.RepoState.AsNoTracking()
                .Where(e => e.ReceivedAtUtc >= cutoff)
                .OrderByDescending(e => e.ReceivedAtUtc)
                .ToList()
                .Select(ToStored)
                .ToList();
        }
    }

    private static RepoStateEntity BuildRow(
        TenantId tenant, string director, string machine, RepoStateSnapshotDto repo, DateTime received)
    {
        var path = Required(repo.Path, "a repository path");
        var branchesJson = JsonSerializer.Serialize(repo.Branches ?? new(), Json);
        var worktreesJson = JsonSerializer.Serialize(repo.Worktrees ?? new(), Json);
        if (branchesJson.Length > MaxListJsonChars)
            throw new RepoStateValidationException($"The branch list for '{path}' is too large.");
        if (worktreesJson.Length > MaxListJsonChars)
            throw new RepoStateValidationException($"The worktree list for '{path}' is too large.");

        var collected = DateTime.SpecifyKind(repo.CollectedAtUtc.ToUniversalTime(), DateTimeKind.Utc);
        // A future collection time is a clock error on the pushing machine, not a real observation; clamp it
        // to the receive time rather than trusting a skewed clock into an age calculation.
        if (collected > received)
            collected = received;

        return new RepoStateEntity
        {
            TenantId = tenant.Value,
            DirectorId = director,
            RepoPath = path,
            MachineName = machine,
            Name = string.IsNullOrWhiteSpace(repo.Name) ? path : repo.Name.Trim(),
            DefaultBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? null : repo.DefaultBranch.Trim(),
            CurrentBranch = string.IsNullOrWhiteSpace(repo.CurrentBranch) ? null : repo.CurrentBranch.Trim(),
            IsDirty = repo.IsDirty,
            CollectedAtUtc = collected,
            ReceivedAtUtc = received,
            BranchesJson = branchesJson,
            WorktreesJson = worktreesJson,
        };
    }

    private static StoredRepoState ToStored(RepoStateEntity e) => new()
    {
        DirectorId = e.DirectorId,
        MachineName = e.MachineName,
        Name = e.Name,
        Path = e.RepoPath,
        DefaultBranch = e.DefaultBranch,
        CurrentBranch = e.CurrentBranch,
        IsDirty = e.IsDirty,
        CollectedAtUtc = e.CollectedAtUtc,
        ReceivedAtUtc = e.ReceivedAtUtc,
        Branches = Deserialize<RepoStateBranchDto>(e.BranchesJson, e.RepoPath, "branches"),
        Worktrees = Deserialize<RepoStateWorktreeDto>(e.WorktreesJson, e.RepoPath, "worktrees"),
    };

    /// <summary>
    /// Read a stored list back. A row whose JSON will not parse is a FAILURE, not an empty list: returning
    /// an empty list would tell the report "this repository has no branches and no worktrees", which is a
    /// clean bill of health invented out of a corrupt row.
    /// </summary>
    private static List<T> Deserialize<T>(string json, string repoPath, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Json)
                   ?? throw new InvalidOperationException("the stored value deserialized to null");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepoStateStore] stored {what} for '{repoPath}' could not be read: {ex.Message}");
            throw new InvalidOperationException(
                $"The stored {what} for '{repoPath}' is unreadable. The Gateway will not report an empty " +
                "list in its place - that would read as a clean repository.", ex);
        }
    }

    private static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new RepoStateValidationException($"A repo-state push needs {field}.");
        var trimmed = value.Trim();
        if (trimmed.Length > MaxIdChars)
            throw new RepoStateValidationException($"The value for {field} is too long (limit {MaxIdChars} characters).");
        return trimmed;
    }
}

/// <summary>One stored repository snapshot, read back for the report. In-process only - there is no public
/// read endpoint for repo state, and the payload never leaves the Gateway except folded into a report row.</summary>
public sealed class StoredRepoState
{
    public string DirectorId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string? DefaultBranch { get; init; }
    public string? CurrentBranch { get; init; }
    public bool IsDirty { get; init; }
    public DateTime CollectedAtUtc { get; init; }
    public DateTime ReceivedAtUtc { get; init; }
    public List<RepoStateBranchDto> Branches { get; init; } = new();
    public List<RepoStateWorktreeDto> Worktrees { get; init; } = new();
}
