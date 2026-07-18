using CcDirector.Core.Utilities;
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

    // ---- authoring (Workflows mission, phase 2) ---------------------------------------------------
    // Drafts are the safety boundary: every write lands on the single mutable draft row; nothing the
    // fleet reads changes until publish, and publish is one SaveChanges (atomic - the hash, the
    // freeze, and the status flips commit together). The draft is a FULL REPLACEMENT on every write.

    /// <summary>Create a new workflow as a draft (invisible to the catalog until published).</summary>
    /// <exception cref="WorkflowValidationException">The content violates the rules (HTTP 400).</exception>
    /// <exception cref="WorkflowConflictException">The id is already taken (HTTP 409).</exception>
    public WorkflowVersionDetailDto CreateDraft(WorkflowContentRequest content)
    {
        WorkflowValidation.ValidateId(content?.Id);
        WorkflowValidation.ValidateDraft(content!);
        var id = content!.Id!.Trim();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            if (ctx.Workflows.Any(h => h.Id == id))
                throw new WorkflowConflictException($"A workflow with id '{id}' already exists.");

            var now = DateTime.UtcNow;
            var head = new WorkflowEntity
            {
                Id = id,
                TenantId = ctx.ActiveTenant!,
                IsBuiltIn = false,
                Archived = false,
                LatestVersion = 1,
                PublishedVersion = null,
                ShippedContentHash = null,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            ctx.Workflows.Add(head);
            var (version, files) = BuildDraftRow(ctx, id, versionNumber: 1, content, now);
            ctx.WorkflowVersions.Add(version);
            ctx.WorkflowFiles.AddRange(files);
            ctx.SaveChanges();

            FileLog.Write($"[WorkflowStore] CreateDraft: id={id}, v1 draft, authoredBy={version.AuthoredBy}");
            return ToVersionDetail(version, files);
        }
    }

    /// <summary>
    /// Replace the workflow's draft content wholesale, minting the draft version if none exists. The
    /// optional If-Match hash is compared against the row the draft builds on (the current draft, or
    /// the published version when the draft is being minted): a mismatch means the caller edited a
    /// stale copy, and the write is refused rather than clobbering someone else's edit.
    /// Null when no such workflow exists.
    /// </summary>
    public WorkflowVersionDetailDto? UpdateDraft(string id, WorkflowContentRequest content, string? ifMatchHash)
    {
        var key = NormalizeId(id);
        WorkflowValidation.ValidateDraft(content);

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var head = ctx.Workflows.FirstOrDefault(h => h.Id == key);
            if (head is null)
                return null;
            if (head.Archived)
                throw new WorkflowValidationException($"Workflow '{key}' is archived.");

            var draft = ctx.WorkflowVersions.FirstOrDefault(
                v => v.WorkflowId == key && v.Status == WorkflowVersionStatus.Draft);
            var baseline = draft ?? ctx.WorkflowVersions.FirstOrDefault(
                v => v.WorkflowId == key && v.Status == WorkflowVersionStatus.Published);
            if (ifMatchHash is not null && baseline is not null &&
                !string.Equals(ifMatchHash, baseline.ContentHash, StringComparison.Ordinal))
                throw new WorkflowConflictException(
                    "The workflow changed since you read it (content hash mismatch). Pull the current " +
                    "content and reapply your edit.");

            var now = DateTime.UtcNow;
            WorkflowVersionEntity row;
            List<WorkflowFileEntity> files;
            if (draft is not null)
            {
                (row, files) = BuildDraftRow(ctx, key, draft.Version, content, draft.CreatedUtc,
                    existing: draft);
                var oldFiles = ctx.WorkflowFiles.Where(f => f.VersionId == draft.Id).ToList();
                ctx.WorkflowFiles.RemoveRange(oldFiles);
                ctx.WorkflowFiles.AddRange(files);
            }
            else
            {
                var nextVersion = head.LatestVersion + 1;
                (row, files) = BuildDraftRow(ctx, key, nextVersion, content, now);
                ctx.WorkflowVersions.Add(row);
                ctx.WorkflowFiles.AddRange(files);
                head.LatestVersion = nextVersion;
            }
            head.UpdatedUtc = now;
            ctx.SaveChanges();

            FileLog.Write($"[WorkflowStore] UpdateDraft: id={key}, v{row.Version}, hash={row.ContentHash[..12]}");
            return ToVersionDetail(row, files);
        }
    }

    /// <summary>Publish the draft: the draft becomes the published version, the previous published
    /// version is superseded, all in one atomic save. Null when no such workflow exists.</summary>
    public WorkflowDto? Publish(string id)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var head = ctx.Workflows.FirstOrDefault(h => h.Id == key);
            if (head is null)
                return null;
            if (head.Archived)
                throw new WorkflowValidationException($"Workflow '{key}' is archived.");

            var draft = ctx.WorkflowVersions.FirstOrDefault(
                v => v.WorkflowId == key && v.Status == WorkflowVersionStatus.Draft);
            if (draft is null)
                throw new WorkflowValidationException(
                    $"Workflow '{key}' has no draft to publish.");
            WorkflowValidation.ValidateForPublish(draft.InstructionsMarkdown, draft.Steps);

            var now = DateTime.UtcNow;
            var previous = ctx.WorkflowVersions.FirstOrDefault(
                v => v.WorkflowId == key && v.Status == WorkflowVersionStatus.Published);
            if (previous is not null)
                previous.Status = WorkflowVersionStatus.Superseded;
            draft.Status = WorkflowVersionStatus.Published;
            draft.PublishedUtc = now;
            head.PublishedVersion = draft.Version;
            head.UpdatedUtc = now;
            ctx.SaveChanges();

            FileLog.Write($"[WorkflowStore] Publish: id={key}, v{draft.Version} published" +
                          (previous is null ? "" : $", v{previous.Version} superseded"));
            return ToDto(head, draft, hasDraft: false);
        }
    }

    /// <summary>Built-ins only: publish the shipped content of the RUNNING binary as a new version
    /// (the "reset to shipped" the ours/yours trade promises). Null when no such workflow exists.</summary>
    public WorkflowDto? ResetToShipped(string id)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var head = ctx.Workflows.FirstOrDefault(h => h.Id == key);
            if (head is null)
                return null;
            if (!head.IsBuiltIn)
                throw new WorkflowValidationException(
                    $"Workflow '{key}' is not built in; reset-to-shipped only applies to built-ins.");

            var definition = BuiltInWorkflows.All().FirstOrDefault(d => d.Id == key)
                ?? throw new InvalidOperationException(
                    $"Built-in workflow '{key}' is not shipped by this binary; cannot reset.");
            var shippedRow = BuiltInWorkflowSeeder.BuildShippedVersion(ctx, definition,
                versionNumber: head.LatestVersion + 1, DateTime.UtcNow);
            shippedRow.AuthoredBy = "gateway:reset";
            shippedRow.ChangeNote = "Reset to the shipped built-in content.";

            var previous = ctx.WorkflowVersions.FirstOrDefault(
                v => v.WorkflowId == key && v.Status == WorkflowVersionStatus.Published);
            if (previous is not null)
                previous.Status = WorkflowVersionStatus.Superseded;
            ctx.WorkflowVersions.Add(shippedRow);
            head.LatestVersion = shippedRow.Version;
            head.PublishedVersion = shippedRow.Version;
            head.ShippedContentHash = shippedRow.ContentHash;
            head.UpdatedUtc = shippedRow.CreatedUtc;
            ctx.SaveChanges();

            FileLog.Write($"[WorkflowStore] ResetToShipped: id={key}, v{shippedRow.Version} published from shipped content");
            var hasDraft = ctx.WorkflowVersions.Any(
                v => v.WorkflowId == key && v.Status == WorkflowVersionStatus.Draft);
            return ToDto(head, shippedRow, hasDraft);
        }
    }

    /// <summary>Archive (soft-delete) a user-defined workflow. Its versions remain - a run that
    /// pinned one must always resolve. False when no such workflow exists.</summary>
    public bool Archive(string id)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var head = ctx.Workflows.FirstOrDefault(h => h.Id == key);
            if (head is null)
                return false;
            if (head.IsBuiltIn)
                throw new WorkflowValidationException(
                    $"Workflow '{key}' is built in and can never be deleted.");

            head.Archived = true;
            head.UpdatedUtc = DateTime.UtcNow;
            ctx.SaveChanges();
            FileLog.Write($"[WorkflowStore] Archive: id={key}");
            return true;
        }
    }

    /// <summary>The version history, newest first, no content bodies. Null when no such workflow.</summary>
    public IReadOnlyList<WorkflowVersionInfoDto>? ListVersions(string id)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            if (!ctx.Workflows.Any(h => h.Id == key))
                return null;
            return ctx.WorkflowVersions.AsNoTracking()
                .Where(v => v.WorkflowId == key)
                .ToList()
                .OrderByDescending(v => v.Version)
                .Select(v => new WorkflowVersionInfoDto
                {
                    Version = v.Version,
                    Status = v.Status,
                    ContentHash = v.ContentHash,
                    AuthoredBy = v.AuthoredBy,
                    ChangeNote = v.ChangeNote,
                    CreatedUtc = v.CreatedUtc,
                    PublishedUtc = v.PublishedUtc,
                })
                .ToList();
        }
    }

    /// <summary>One version's complete content snapshot. Null when absent.</summary>
    public WorkflowVersionDetailDto? GetVersionDetail(string id, int version)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var row = ctx.WorkflowVersions.AsNoTracking().FirstOrDefault(
                v => v.WorkflowId == key && v.Version == version);
            if (row is null)
                return null;
            var files = ctx.WorkflowFiles.AsNoTracking().Where(f => f.VersionId == row.Id).ToList();
            return ToVersionDetail(row, files);
        }
    }

    /// <summary>
    /// The raw instruction markdown - the agent read path. With an explicit version, any PUBLISHED or
    /// SUPERSEDED row serves (immutable history; a pinned run must always resolve, archived or not) -
    /// but never the draft: a draft is mutable, so serving it as pinned history would be a lie, and
    /// the publish boundary is exactly what makes content fleet-readable. (Authoring reads a draft
    /// through the versions/{n} detail route, which states the status.) Without a version, the
    /// published version of a live workflow serves; a draft-only or archived workflow yields null.
    /// </summary>
    public string? GetInstructions(string id, int? version)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var row = ResolveVersionRow(ctx, key, version);
            return row?.InstructionsMarkdown;
        }
    }

    /// <summary>One helper file's raw content, resolved like <see cref="GetInstructions"/>.</summary>
    public string? GetFileContent(string id, string fileName, int? version)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var row = ResolveVersionRow(ctx, key, version);
            if (row is null)
                return null;
            return ctx.WorkflowFiles.AsNoTracking()
                .FirstOrDefault(f => f.VersionId == row.Id && f.FileName == fileName)?.Content;
        }
    }

    private WorkflowVersionEntity? ResolveVersionRow(GatewayDbContext ctx, string key, int? version)
    {
        if (version.HasValue)
            return ctx.WorkflowVersions.AsNoTracking().FirstOrDefault(
                v => v.WorkflowId == key && v.Version == version.Value &&
                     v.Status != WorkflowVersionStatus.Draft);

        var head = ctx.Workflows.AsNoTracking().FirstOrDefault(h => h.Id == key);
        if (head is null || head.Archived || head.PublishedVersion is null)
            return null;
        return ctx.WorkflowVersions.AsNoTracking().FirstOrDefault(
            v => v.WorkflowId == key && v.Status == WorkflowVersionStatus.Published);
    }

    /// <summary>Build (or rebuild, for an existing draft) the draft version row + its file rows from a
    /// full-replacement content request, computing the canonical bundle hash.</summary>
    private static (WorkflowVersionEntity Row, List<WorkflowFileEntity> Files) BuildDraftRow(
        GatewayDbContext ctx,
        string workflowId,
        int versionNumber,
        WorkflowContentRequest content,
        DateTime createdUtc,
        WorkflowVersionEntity? existing = null)
    {
        var steps = (content.Steps ?? new List<WorkflowStepDto>()).Select(s => new WorkflowStepDto
        {
            Name = s.Name,
            Description = s.Description,
            Doer = s.Doer,
            Reviewer = s.Reviewer,
            Done = s.Done,
        }).ToList();
        var criteria = (content.OutcomeCriteria ?? new List<WorkflowOutcomeCriterionDto>())
            .Select(c => new WorkflowOutcomeCriterionDto
            {
                CriterionId = c.CriterionId,
                Description = c.Description,
                ProofHint = c.ProofHint,
            }).ToList();
        var instructions = content.InstructionsMarkdown ?? "";
        var filePayloads = content.Files ?? new List<WorkflowFileDto>();
        var hashedFiles = filePayloads
            .Select(f => (f.FileName, Content: f.Content ?? "", Hash: WorkflowContentHash.ForFile(f.Content ?? "")))
            .ToList();
        var contentHash = WorkflowContentHash.ForBundle(
            content.Name!, content.Summary!, content.WhenToUse ?? "", content.HumanCheckpoint ?? "",
            steps, criteria, instructions, hashedFiles.Select(f => (f.FileName, f.Hash)));

        var row = existing ?? new WorkflowVersionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = ctx.ActiveTenant!,
            WorkflowId = workflowId,
            Version = versionNumber,
            Status = WorkflowVersionStatus.Draft,
            CreatedUtc = createdUtc,
        };
        row.Name = content.Name!;
        row.Summary = content.Summary!;
        row.WhenToUse = content.WhenToUse ?? "";
        row.HumanCheckpoint = content.HumanCheckpoint ?? "";
        row.Steps = steps;
        row.InstructionsMarkdown = instructions;
        row.OutcomeCriteria = criteria;
        row.ContentHash = contentHash;
        row.AuthoredBy = string.IsNullOrWhiteSpace(content.AuthoredBy) ? "unknown" : content.AuthoredBy!;
        row.ChangeNote = content.ChangeNote;

        var files = hashedFiles.Select(f => new WorkflowFileEntity
        {
            Id = Guid.NewGuid(),
            TenantId = ctx.ActiveTenant!,
            VersionId = row.Id,
            FileName = f.FileName,
            Content = f.Content,
            ContentHash = f.Hash,
        }).ToList();
        return (row, files);
    }

    private static WorkflowVersionDetailDto ToVersionDetail(
        WorkflowVersionEntity row, IReadOnlyList<WorkflowFileEntity> files) => new()
    {
        WorkflowId = row.WorkflowId,
        Version = row.Version,
        Status = row.Status,
        Name = row.Name,
        Summary = row.Summary,
        WhenToUse = row.WhenToUse,
        HumanCheckpoint = row.HumanCheckpoint,
        Steps = row.Steps.Select(s => new WorkflowStepDto
        {
            Name = s.Name,
            Description = s.Description,
            Doer = s.Doer,
            Reviewer = s.Reviewer,
            Done = s.Done,
        }).ToList(),
        InstructionsMarkdown = row.InstructionsMarkdown,
        OutcomeCriteria = row.OutcomeCriteria.Select(c => new WorkflowOutcomeCriterionDto
        {
            CriterionId = c.CriterionId,
            Description = c.Description,
            ProofHint = c.ProofHint,
        }).ToList(),
        Files = files.Select(f => new WorkflowFileInfoDto
        {
            FileName = f.FileName,
            ContentHash = f.ContentHash,
            Content = f.Content,
        }).ToList(),
        ContentHash = row.ContentHash,
        AuthoredBy = row.AuthoredBy,
        ChangeNote = row.ChangeNote,
        CreatedUtc = row.CreatedUtc,
        PublishedUtc = row.PublishedUtc,
    };

    private static string NormalizeId(string id) => (id ?? "").Trim().ToLowerInvariant();

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
