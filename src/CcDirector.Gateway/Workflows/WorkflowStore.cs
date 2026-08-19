using CcDirector.Core.Tenancy;
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
/// catalog shape (issue #1617) plus additive fields.
///
/// THE SHARED WORKFLOW LIBRARY (devthrottle_internal issue 514). The built-ins are DevThrottle's:
/// seeded once into the partition that is ambient at CONSTRUCTION (the reserved System tenant on the
/// hosted Gateway, Local on self-host) and served READ-ONLY to every tenant from there. A tenant's
/// catalog is the shared built-ins UNION the tenant's own workflows; id-scoped reads resolve the
/// owning partition (the library always wins - it can never be shadowed by a tenant workflow, and
/// creating a workflow under a built-in id is refused). This is the deliberate cross-tenant read
/// named <see cref="Tenancy.SystemCapabilityAllowlist.SharedWorkflowLibraryRead"/>: it reaches
/// exactly one foreign partition (the library), for built-in rows only, and never writes there on a
/// request path - the ONLY writer of library content is the boot-time seeder.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over
/// a fresh pooled context, preserving the single-writer invariant.
/// </summary>
public sealed class WorkflowStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The partition the built-in library lives in: the tenant that was ambient when this
    /// store was constructed (System on hosted, Local on self-host). Captured rather than hard-coded
    /// so self-host - where the single-tenant context answers Local for everything - keeps reading
    /// the exact rows it always read, and the reserved System id never appears outside the hosted
    /// composition root.</summary>
    // Captured by InitializeCore, which may run after construction (the Gateway defers it so its listener
    // can bind first), so this cannot be readonly. It is still written EXACTLY ONCE: InitializeCore is
    // guarded by _initialized and never runs twice. Empty until then, and nothing reads it before the
    // readiness gate opens.
    private string _libraryTenant = "";

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <param name="deferInitialize">
    /// When true the constructor stops after wiring; the caller must call <see cref="Initialize"/> once the
    /// database is open. The Gateway passes true so its listener can bind BEFORE any database work.
    ///
    /// The caller MUST run Initialize inside the same ambient tenant scope the constructor would have had -
    /// the library partition is captured from it below, and capturing it under a different scope would put
    /// the shared workflow library in the wrong tenant.
    /// </param>
    public WorkflowStore(GatewayDatabase db, bool deferInitialize = false)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));

        if (!deferInitialize)
            InitializeCore();
    }

    /// <summary>Run the deferred load. Idempotent.</summary>
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
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            _libraryTenant = ctx.ActiveTenant!;
            BuiltInWorkflowSeeder.Seed(ctx);
        }
        _initialized = true;
    }

    /// <summary>A fresh context scoped to the shared library partition. Read-only by contract on
    /// request paths (the seeder is the only library writer); caller disposes.</summary>
    private GatewayDbContext CreateLibraryContext() => _db.CreateContext(new TenantId(_libraryTenant));

    /// <summary>
    /// Open the context for the partition that OWNS workflow <paramref name="key"/>: the shared
    /// library when the id is a built-in there (the library always wins, so it can never be shadowed),
    /// otherwise the ambient tenant's own partition. On self-host both are the same partition and this
    /// degenerates to today's single read. Caller disposes.
    /// </summary>
    private GatewayDbContext OpenOwningContext(string key)
    {
        var library = CreateLibraryContext();
        if (library.Workflows.AsNoTracking().Any(h => h.Id == key && h.IsBuiltIn))
            return library;
        library.Dispose();
        return _db.CreateContext();
    }

    /// <summary>True when <paramref name="key"/> is a built-in living in the shared library.</summary>
    private bool IsLibraryBuiltIn(string key)
    {
        using var library = CreateLibraryContext();
        return library.Workflows.AsNoTracking().Any(h => h.Id == key && h.IsBuiltIn);
    }

    /// <summary>The ambient tenant's on/off choice for a built-in, or null when the tenant has made
    /// none (the workflow serves with the library's shipped state). Phase 2 of the shared library:
    /// the choice lives in the TENANT's partition because the shared head row is read-only to tenants.</summary>
    private bool? TenantChoiceFor(string key)
    {
        using var ctx = _db.CreateContext();
        return ctx.WorkflowTenantOverrides.AsNoTracking()
            .Where(o => o.WorkflowId == key)
            .Select(o => (bool?)o.Enabled)
            .FirstOrDefault();
    }

    /// <summary>The ambient tenant's on/off choices for a set of built-in ids, absent ids omitted.</summary>
    private Dictionary<string, bool> TenantChoicesFor(IReadOnlyCollection<string> keys)
    {
        using var ctx = _db.CreateContext();
        return ctx.WorkflowTenantOverrides.AsNoTracking()
            .Where(o => keys.Contains(o.WorkflowId))
            .ToList()
            .ToDictionary(o => o.WorkflowId, o => o.Enabled, StringComparer.Ordinal);
    }

    /// <summary>A built-in head's enabled state as THIS tenant sees it: the tenant's own choice when
    /// one exists, else the library's state. User-owned heads keep their head-row switch untouched.</summary>
    private bool EffectiveEnabled(WorkflowEntity head) =>
        head.IsBuiltIn ? TenantChoiceFor(head.Id) ?? head.Enabled : head.Enabled;

    /// <summary>
    /// Built-ins are READ-ONLY (Shared Workflow Library phase 3, owner ruling 2026-07-24, reversing
    /// the 2026-07-17 editable-with-reset ruling): they are DevThrottle's, maintained by shipping a
    /// new binary, and the only writer of their content is the boot-time seeder. Every authoring
    /// write refuses a built-in id with the pointer at the sanctioned customization path - clone.
    /// </summary>
    private void RefuseBuiltInEdit(string key, string verb)
    {
        if (IsLibraryBuiltIn(key))
            throw new WorkflowValidationException(
                $"'{key}' is a built-in DevThrottle workflow and cannot be {verb}. Built-ins are " +
                "maintained by DevThrottle and update with the Gateway itself. To customize the " +
                "conduct, clone it into your own workflow: cc-devthrottle workflow clone " + key +
                " <new-id>");
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
            // The shared library first (built-ins, in their shipped creation order), then the ambient
            // tenant's own workflows. On self-host both passes read the same partition, split by
            // IsBuiltIn, so the served set and order are exactly what the single read served before.
            var result = new List<WorkflowDto>();
            var builtInIds = new HashSet<string>(StringComparer.Ordinal);

            using (var library = CreateLibraryContext())
            {
                foreach (var dto in ReadPublished(library, builtIns: true, exclude: null))
                {
                    builtInIds.Add(dto.Id);
                    result.Add(dto);
                }
            }

            // Fold this tenant's own on/off choices over the library's shipped state (phase 2): at
            // this point the result holds ONLY the built-ins, so the stamp cannot touch a user row.
            var choices = TenantChoicesFor(builtInIds);
            foreach (var dto in result)
            {
                if (choices.TryGetValue(dto.Id, out var chosen))
                    dto.Enabled = chosen;
            }

            using (var ctx = _db.CreateContext())
            {
                // The exclusion is the shadow guard's belt-and-suspenders: a tenant row that somehow
                // shares a built-in id (created before the refusal existed) must not double-list.
                result.AddRange(ReadPublished(ctx, builtIns: false, exclude: builtInIds));
            }

            return result;
        }
    }

    /// <summary>One catalog pass over one partition: published heads (built-ins or user workflows),
    /// projected from their published versions, in creation order.</summary>
    private static IReadOnlyList<WorkflowDto> ReadPublished(
        GatewayDbContext ctx, bool builtIns, HashSet<string>? exclude)
    {
        var heads = ctx.Workflows.AsNoTracking()
            .Where(h => h.IsBuiltIn == builtIns && !h.Archived && h.PublishedVersion != null)
            .ToList()
            .Where(h => exclude is null || !exclude.Contains(h.Id))
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
            using var ctx = OpenOwningContext(key);
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
            var dto = ToDto(head, version, hasDraft);
            if (head.IsBuiltIn)
                dto.Enabled = EffectiveEnabled(head); // fold the tenant's own choice (phase 2)
            return dto;
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
            // The library can never be shadowed: a tenant creating "mission" would otherwise mint a
            // workflow the resolution rules could not serve (the built-in always wins by id).
            if (IsLibraryBuiltIn(id))
                throw new WorkflowConflictException(
                    $"'{id}' is a built-in DevThrottle workflow; its id is reserved. Create your " +
                    "workflow under a different id.");

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
            RefuseBuiltInEdit(key, "edited");
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
            RefuseBuiltInEdit(key, "published over");
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

    // Reset-to-shipped was RETIRED in the Shared Workflow Library phase 3 (owner ruling 2026-07-24):
    // built-ins are read-only, so they can never diverge from shipped content and there is nothing to
    // reset. The seeder always republishes the running binary's content on a hash change.

    /// <summary>
    /// Clone a workflow's PUBLISHED content into a new tenant-owned workflow (Shared Workflow
    /// Library phase 4 - the sanctioned customization path for the read-only built-ins, and equally
    /// usable on the tenant's own workflows). The clone copies everything the published version
    /// carries - steps, instructions, outcome criteria, helper files - into version 1 of a fresh id,
    /// born PUBLISHED (the content just came off a published bundle, so it is immediately runnable)
    /// and fully editable; provenance (source id and version) is recorded on the version row's
    /// change note. Null when the source does not exist or has no published version.
    /// </summary>
    /// <exception cref="WorkflowValidationException">The new id is invalid.</exception>
    /// <exception cref="WorkflowConflictException">The new id is taken or is a built-in id.</exception>
    public WorkflowDto? Clone(string sourceId, string newId, string authoredBy)
    {
        var sourceKey = NormalizeId(sourceId);
        WorkflowValidation.ValidateId(newId);
        var id = newId.Trim();

        lock (_gate)
        {
            // The clone's id obeys the same rules as create: never a built-in id (the library can
            // never be shadowed), never a taken id.
            if (IsLibraryBuiltIn(id))
                throw new WorkflowConflictException(
                    $"'{id}' is a built-in DevThrottle workflow; its id is reserved. Pick a different id.");

            WorkflowVersionEntity sourceVersion;
            List<WorkflowFileEntity> sourceFiles;
            using (var src = OpenOwningContext(sourceKey))
            {
                var sourceHead = src.Workflows.AsNoTracking().FirstOrDefault(h => h.Id == sourceKey);
                if (sourceHead is null || sourceHead.Archived || sourceHead.PublishedVersion is null)
                    return null;
                sourceVersion = src.WorkflowVersions.AsNoTracking().First(
                    v => v.WorkflowId == sourceKey && v.Status == WorkflowVersionStatus.Published);
                sourceFiles = src.WorkflowFiles.AsNoTracking()
                    .Where(f => f.VersionId == sourceVersion.Id)
                    .ToList();
            }

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
                PublishedVersion = 1,
                ShippedContentHash = null,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            var version = new WorkflowVersionEntity
            {
                TenantId = ctx.ActiveTenant!,
                WorkflowId = id,
                Version = 1,
                Status = WorkflowVersionStatus.Published,
                Name = sourceVersion.Name,
                Summary = sourceVersion.Summary,
                WhenToUse = sourceVersion.WhenToUse,
                HumanCheckpoint = sourceVersion.HumanCheckpoint,
                Steps = sourceVersion.Steps.Select(s => new WorkflowStepDto
                {
                    Name = s.Name,
                    Description = s.Description,
                    Doer = s.Doer,
                    Reviewer = s.Reviewer,
                    Done = s.Done,
                }).ToList(),
                InstructionsMarkdown = sourceVersion.InstructionsMarkdown,
                OutcomeCriteria = sourceVersion.OutcomeCriteria.Select(c => new WorkflowOutcomeCriterionDto
                {
                    CriterionId = c.CriterionId,
                    Description = c.Description,
                    ProofHint = c.ProofHint,
                }).ToList(),
                ContentHash = sourceVersion.ContentHash, // identical content, identical bundle hash
                AuthoredBy = string.IsNullOrWhiteSpace(authoredBy) ? "unknown" : authoredBy.Trim(),
                ChangeNote = $"Cloned from '{sourceKey}' v{sourceVersion.Version}.",
                CreatedUtc = now,
                PublishedUtc = now,
            };
            var files = sourceFiles.Select(f => new WorkflowFileEntity
            {
                TenantId = ctx.ActiveTenant!,
                VersionId = version.Id,
                FileName = f.FileName,
                Content = f.Content,
                ContentHash = f.ContentHash,
            }).ToList();

            ctx.Workflows.Add(head);
            ctx.WorkflowVersions.Add(version);
            ctx.WorkflowFiles.AddRange(files);
            ctx.SaveChanges();

            FileLog.Write($"[WorkflowStore] Clone: '{sourceKey}' v{sourceVersion.Version} -> '{id}' v1 " +
                          $"({files.Count} files), authoredBy={version.AuthoredBy}");
            return ToDto(head, version, hasDraft: false);
        }
    }

    /// <summary>Archive (soft-delete) a user-defined workflow. Its versions remain - a run that
    /// pinned one must always resolve. False when no such workflow exists.</summary>
    public bool Archive(string id)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            if (IsLibraryBuiltIn(key))
                throw new WorkflowValidationException(
                    $"Workflow '{key}' is built in and can never be deleted.");

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
            using var ctx = OpenOwningContext(key);
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
            using var ctx = OpenOwningContext(key);
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
    /// published version of a live workflow serves; a draft-only or archived workflow yields null,
    /// and a workflow the owner turned OFF refuses with a clear message - never a misleading 404.
    /// </summary>
    /// <exception cref="WorkflowValidationException">The workflow exists but is OFF and no explicit
    /// version was named. Pinned reads (explicit version) keep resolving - a seated run's conduct
    /// never disappears under it - but the default head read is exactly the "use this workflow" path
    /// the owner switched off.</exception>
    public string? GetInstructions(string id, int? version)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = OpenOwningContext(key);
            if (version is null)
            {
                var head = ctx.Workflows.AsNoTracking().FirstOrDefault(h => h.Id == key);
                if (head is { Archived: false } && !EffectiveEnabled(head))
                    throw new WorkflowValidationException(
                        $"Workflow '{key}' is turned OFF on this fleet. The owner disabled it; its " +
                        "history remains readable by explicit version, and it can be re-enabled in " +
                        "the cockpit or with: cc-devthrottle workflow enable " + key);
            }
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
            using var ctx = OpenOwningContext(key);
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
        Enabled = head.Enabled,
        // The Gateway's editability verdict (phase 3): built-ins are read-only, tenant-owned
        // workflows are the caller's to edit. Clients render this verbatim.
        Editable = !head.IsBuiltIn,
    };

    /// <summary>
    /// The owner's switch (register redesign, owner ruling 2026-07-18): turn a workflow on or off -
    /// built-ins included, because DevThrottle configures to what the USER wants. Off hides the
    /// workflow from agents' launch briefings, refuses default conduct reads and new runs/seats, and
    /// deletes NOTHING; the flip is instant both ways for every subsequent read fleet-wide. False
    /// when no such workflow exists (archived counts as gone).
    /// </summary>
    public bool SetEnabled(string id, bool enabled, string by)
    {
        // A governance change has an actor, always - the same posture as run acceptance (#1771):
        // who flipped the fleet's rules is never allowed to be nobody.
        if (string.IsNullOrWhiteSpace(by))
            throw new WorkflowValidationException(
                "Turning a workflow on or off requires 'by' - who is making the change.");
        var key = NormalizeId(id);
        lock (_gate)
        {
            // A BUILT-IN's switch is per tenant (phase 2): the shared library head row is read-only
            // to tenants, so the choice upserts into the tenant's own override row and the read
            // paths fold it. One authority per id: the override for library workflows, the head row
            // (below) for tenant-owned ones.
            if (IsLibraryBuiltIn(key))
            {
                using var ctx = _db.CreateContext();
                var choice = ctx.WorkflowTenantOverrides.FirstOrDefault(o => o.WorkflowId == key);
                if (choice is null)
                {
                    choice = new WorkflowTenantOverrideEntity
                    {
                        TenantId = ctx.ActiveTenant!,
                        WorkflowId = key,
                    };
                    ctx.WorkflowTenantOverrides.Add(choice);
                }
                choice.Enabled = enabled;
                choice.UpdatedBy = by.Trim();
                choice.UpdatedAtUtc = DateTime.UtcNow;
                ctx.SaveChanges();
                FileLog.Write($"[WorkflowStore] SetEnabled: id={key}, enabled={enabled}, by={by.Trim()} " +
                              "(built-in - per-tenant override)");
                return true;
            }

            using (var ctx = _db.CreateContext())
            {
                var head = ctx.Workflows.FirstOrDefault(h => h.Id == key);
                if (head is null || head.Archived)
                    return false;

                head.Enabled = enabled;
                head.UpdatedUtc = DateTime.UtcNow;
                ctx.SaveChanges();
                // Attribution is log-based for now; the durable audit trail is governance's append-only
                // event ledger (#1771), which will hang off these same ids.
                FileLog.Write($"[WorkflowStore] SetEnabled: id={key}, enabled={enabled}, by={by.Trim()}");
                return true;
            }
        }
    }
}
