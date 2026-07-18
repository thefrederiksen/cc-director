using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Workflows;

/// <summary>
/// The Gateway's persisted workflow catalog (Workflows mission, phase 1). Workflows live in the EF
/// data layer (<c>workflows</c> / <c>workflow_versions</c> / <c>workflow_files</c>), NOT as C#
/// literals: the head row is identity/lifecycle, every piece of content is an immutable version row,
/// and the built-in set (<see cref="BuiltInWorkflows"/>) is written in by
/// <see cref="BuiltInWorkflowSeeder"/> at construction. The read surface serves the exact legacy
/// catalog shape (issue #1617) plus additive fields; authoring (drafts, publish, files) is the next
/// phase on this store.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over
/// a fresh pooled context, preserving the single-writer invariant.
/// </summary>
public sealed class WorkflowStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public WorkflowStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            BuiltInWorkflowSeeder.Seed(ctx);
        }
    }

    /// <summary>
    /// Every workflow with a published version, projected from that published version, in stable
    /// catalog order (creation order; the seeder stamps the built-ins so they keep their shipped
    /// order). Draft-only and archived workflows are omitted - the catalog lists what the fleet can
    /// actually run.
    /// </summary>
    public IReadOnlyList<WorkflowDto> ListPublished()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var heads = ctx.Workflows.AsNoTracking()
                .Where(h => !h.Archived && h.PublishedVersion != null)
                .ToList()
                .OrderBy(h => h.CreatedUtc)
                .ThenBy(h => h.Id, StringComparer.Ordinal)
                .ToList();
            if (heads.Count == 0)
                return Array.Empty<WorkflowDto>();

            var ids = heads.Select(h => h.Id).ToList();
            var versions = ctx.WorkflowVersions.AsNoTracking()
                .Where(v => ids.Contains(v.WorkflowId) && v.Status == WorkflowVersionStatus.Published)
                .ToList()
                .ToDictionary(v => v.WorkflowId, StringComparer.Ordinal);
            var draftIds = ctx.WorkflowVersions.AsNoTracking()
                .Where(v => v.Status == WorkflowVersionStatus.Draft)
                .Select(v => v.WorkflowId)
                .ToHashSet(StringComparer.Ordinal);

            return heads
                .Select(h => ToDto(h, versions[h.Id], draftIds.Contains(h.Id)))
                .ToList();
        }
    }

    /// <summary>
    /// One workflow by id (case-insensitive, matching the legacy endpoint), projected from its
    /// published version. Null when the workflow is absent, archived, or has never been published.
    /// </summary>
    public WorkflowDto? GetPublished(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var key = id.Trim().ToLowerInvariant();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var head = ctx.Workflows.AsNoTracking().FirstOrDefault(h => h.Id == key);
            if (head is null || head.Archived || head.PublishedVersion is null)
                return null;

            var version = ctx.WorkflowVersions.AsNoTracking().FirstOrDefault(
                v => v.WorkflowId == head.Id && v.Status == WorkflowVersionStatus.Published);
            if (version is null)
                throw new InvalidOperationException(
                    $"Workflow '{head.Id}' claims published version {head.PublishedVersion} but no " +
                    "published version row exists. The workflow store is inconsistent; refusing to " +
                    "serve a half-workflow.");

            var hasDraft = ctx.WorkflowVersions.AsNoTracking().Any(
                v => v.WorkflowId == head.Id && v.Status == WorkflowVersionStatus.Draft);
            return ToDto(head, version, hasDraft);
        }
    }

    private static WorkflowDto ToDto(WorkflowEntity head, WorkflowVersionEntity version, bool hasDraft) => new()
    {
        Id = head.Id,
        Name = version.Name,
        Summary = version.Summary,
        WhenToUse = version.WhenToUse,
        HumanCheckpoint = version.HumanCheckpoint,
        Steps = version.Steps.Select(s => new WorkflowStepDto
        {
            Name = s.Name,
            Description = s.Description,
            Doer = s.Doer,
            Reviewer = s.Reviewer,
            Done = s.Done,
        }).ToList(),
        Version = version.Version,
        IsBuiltIn = head.IsBuiltIn,
        UpdatedUtc = head.UpdatedUtc,
        HasDraft = hasDraft,
        ContentHash = version.ContentHash,
    };
}
