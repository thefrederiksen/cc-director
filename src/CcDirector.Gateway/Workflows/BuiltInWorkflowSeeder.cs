using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Workflows;

/// <summary>
/// Writes the shipped built-in workflows (<see cref="BuiltInWorkflows"/> + their embedded instruction
/// bodies) into the persisted workflow store at startup. Built-ins are READ-ONLY (Shared Workflow
/// Library phase 3, owner ruling 2026-07-24, reversing the 2026-07-17 editable-with-reset trade):
/// they are DevThrottle's, this seeder is the ONLY writer of their content, and the catalog always
/// tracks the RUNNING binary:
///
///  - Absent workflow: seeded as version 1, published, with the shipped content.
///  - Present with a different published hash than this binary ships: the shipped bundle is
///    published as the next version and the previous one is superseded - in BOTH directions, so a
///    rollback republishes the older conduct and the minted version row is the honest record of what
///    the fleet was served. Superseded versions remain readable forever (pinned runs depend on it) -
///    including any edit published under the OLD editable-built-ins ruling, which is preserved as
///    history while shipped content takes over the head.
///
/// Built-ins keep their shipped catalog order via deliberately staggered CreatedUtc stamps (the
/// catalog lists in creation order).
/// </summary>
public static class BuiltInWorkflowSeeder
{
    /// <summary>Seed or upgrade every built-in workflow inside the given context. Called by the
    /// store's constructor under its write lock; saves once per changed workflow.</summary>
    public static void Seed(GatewayDbContext ctx)
    {
        if (ctx is null)
            throw new ArgumentNullException(nameof(ctx));

        var definitions = BuiltInWorkflows.All();
        // Staggered creation stamps preserve the shipped order under the catalog's creation-order
        // listing; existing rows keep the stamp they were first seeded with.
        var baseUtc = DateTime.UtcNow;

        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            var shippedHash = ShippedHash(definition);

            var head = ctx.Workflows.FirstOrDefault(h => h.Id == definition.Id);
            if (head is null)
            {
                SeedFresh(ctx, definition, shippedHash, baseUtc.AddMilliseconds(i));
                continue;
            }

            // Read-only built-ins (phase 3): the invariant is that the PUBLISHED version IS the
            // shipped bundle. Compare the published hash - not merely the recorded shipped hash -
            // so a customization published under the old editable-built-ins ruling is superseded by
            // shipped content even when the binary's own content did not change across the upgrade.
            var published = ctx.WorkflowVersions.AsNoTracking().FirstOrDefault(
                v => v.WorkflowId == head.Id && v.Status == WorkflowVersionStatus.Published);
            if (published is not null &&
                string.Equals(published.ContentHash, shippedHash, StringComparison.Ordinal))
            {
                if (!string.Equals(head.ShippedContentHash, shippedHash, StringComparison.Ordinal))
                {
                    head.ShippedContentHash = shippedHash; // heal a stale record; content already right
                    ctx.SaveChanges();
                }
                continue; // the fleet is already served exactly what this binary ships.
            }

            UpgradeShippedContent(ctx, head, definition, shippedHash);
        }
    }

    /// <summary>The canonical bundle hash of what THIS binary ships for a built-in definition.</summary>
    public static string ShippedHash(WorkflowDefinition definition)
    {
        var instructions = BuiltInWorkflows.InstructionsFor(definition.Id);
        return WorkflowContentHash.ForBundle(
            definition.Name, definition.Summary, definition.WhenToUse, definition.HumanCheckpoint,
            ShippedSteps(definition), Array.Empty<WorkflowOutcomeCriterionDto>(), instructions,
            Array.Empty<(string, string)>());
    }

    /// <summary>
    /// Build a fully-populated PUBLISHED version row from the running binary's shipped content for a
    /// built-in definition. Shared by the seeder (initial seed + uncustomized upgrade) and the
    /// store's reset-to-shipped, so "what shipped content becomes as a version row" has exactly one
    /// definition.
    /// </summary>
    public static WorkflowVersionEntity BuildShippedVersion(
        GatewayDbContext ctx, WorkflowDefinition definition, int versionNumber, DateTime createdUtc) => new()
    {
        TenantId = ctx.ActiveTenant!,
        WorkflowId = definition.Id,
        Version = versionNumber,
        Status = WorkflowVersionStatus.Published,
        Name = definition.Name,
        Summary = definition.Summary,
        WhenToUse = definition.WhenToUse,
        HumanCheckpoint = definition.HumanCheckpoint,
        Steps = ShippedSteps(definition),
        InstructionsMarkdown = BuiltInWorkflows.InstructionsFor(definition.Id),
        OutcomeCriteria = new List<WorkflowOutcomeCriterionDto>(),
        ContentHash = ShippedHash(definition),
        AuthoredBy = "gateway:shipped",
        ChangeNote = "Shipped built-in content.",
        CreatedUtc = createdUtc,
        PublishedUtc = createdUtc,
    };

    private static List<WorkflowStepDto> ShippedSteps(WorkflowDefinition definition) =>
        definition.Steps.Select(s => new WorkflowStepDto
        {
            Name = s.Name,
            Description = s.Description,
            Doer = s.Doer,
            Reviewer = s.Reviewer,
            Done = s.Done,
        }).ToList();

    private static void SeedFresh(
        GatewayDbContext ctx,
        WorkflowDefinition definition,
        string shippedHash,
        DateTime createdUtc)
    {
        var head = new WorkflowEntity
        {
            Id = definition.Id,
            TenantId = ctx.ActiveTenant!,
            IsBuiltIn = true,
            Archived = false,
            LatestVersion = 1,
            PublishedVersion = 1,
            ShippedContentHash = shippedHash,
            CreatedUtc = createdUtc,
            UpdatedUtc = createdUtc,
        };
        ctx.Workflows.Add(head);
        ctx.WorkflowVersions.Add(BuildShippedVersion(ctx, definition, versionNumber: 1, createdUtc));
        ctx.SaveChanges();
        FileLog.Write($"[BuiltInWorkflowSeeder] Seeded built-in workflow: id={definition.Id}, v1, hash={shippedHash[..12]}");
    }

    private static void UpgradeShippedContent(
        GatewayDbContext ctx,
        WorkflowEntity head,
        WorkflowDefinition definition,
        string shippedHash)
    {
        // Built-ins are read-only (phase 3): the catalog always tracks the RUNNING binary, so a hash
        // difference - in either direction - publishes this binary's bundle as the next version. Any
        // previously published content (including an edit made under the old editable-built-ins
        // ruling) is preserved as a superseded, forever-readable version row, never rewritten.
        var published = ctx.WorkflowVersions.FirstOrDefault(
            v => v.WorkflowId == head.Id && v.Status == WorkflowVersionStatus.Published);

        var now = DateTime.UtcNow;
        if (published is not null)
            published.Status = WorkflowVersionStatus.Superseded;
        var nextVersion = head.LatestVersion + 1;
        ctx.WorkflowVersions.Add(BuildShippedVersion(ctx, definition, nextVersion, now));
        head.LatestVersion = nextVersion;
        head.PublishedVersion = nextVersion;
        head.UpdatedUtc = now;
        FileLog.Write($"[BuiltInWorkflowSeeder] Upgraded built-in workflow: id={head.Id}, " +
                      $"v{nextVersion} published from shipped content, hash={shippedHash[..12]}");

        head.ShippedContentHash = shippedHash;
        ctx.SaveChanges();
    }

}
