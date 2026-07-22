using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Workflows;

/// <summary>
/// Writes the shipped built-in workflows (<see cref="BuiltInWorkflows"/> + their embedded instruction
/// bodies) into the persisted workflow store at startup. The rules are the injected-text ours/yours
/// trade, applied per workflow (owner ruling, 2026-07-17: built-ins are editable with
/// reset-to-shipped):
///
///  - Absent workflow: seeded as version 1, published, with the shipped content.
///  - Present and UNCUSTOMIZED (its published content hash equals the shipped hash we last recorded):
///    a binary that ships different content auto-publishes it as the next version - the user follows
///    "ours" and the catalog tracks the RUNNING binary, in both directions. A rollback deliberately
///    rolls the conduct back too (and mints a version recording that), exactly as the injected-text
///    "ours" channel serves whatever the running binary carries.
///  - Present and CUSTOMIZED (the user published their own edit): left completely alone. The newly
///    shipped hash is still recorded on the head so a later "reset to shipped" knows what this binary
///    ships, but no version is created and nothing the user wrote is touched.
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

            if (string.Equals(head.ShippedContentHash, shippedHash, StringComparison.Ordinal))
                continue; // this binary ships exactly what was last recorded - nothing to do.

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
        var published = ctx.WorkflowVersions.FirstOrDefault(
            v => v.WorkflowId == head.Id && v.Status == WorkflowVersionStatus.Published);
        var uncustomized = published is not null &&
            string.Equals(published.ContentHash, head.ShippedContentHash, StringComparison.Ordinal);

        if (uncustomized)
        {
            // The user follows the shipped content - publish THIS binary's bundle as the next version
            // so every Director picks it up, exactly like the injected-text "ours" channel. This is
            // direction-agnostic on purpose: a rollback republishes the older conduct, and the minted
            // version row is the honest record of what the fleet was served.
            var now = DateTime.UtcNow;
            published!.Status = WorkflowVersionStatus.Superseded;
            var nextVersion = head.LatestVersion + 1;
            ctx.WorkflowVersions.Add(BuildShippedVersion(ctx, definition, nextVersion, now));
            head.LatestVersion = nextVersion;
            head.PublishedVersion = nextVersion;
            head.UpdatedUtc = now;
            FileLog.Write($"[BuiltInWorkflowSeeder] Upgraded built-in workflow: id={head.Id}, " +
                          $"v{nextVersion} published from shipped content, hash={shippedHash[..12]}");
        }
        else
        {
            // Customized: the user's published edit stays untouched. Record what this binary ships so
            // a later reset-to-shipped can restore it.
            FileLog.Write($"[BuiltInWorkflowSeeder] Built-in workflow {head.Id} is customized; shipped " +
                          $"content NOT applied (recorded hash={shippedHash[..12]} for reset)");
        }

        head.ShippedContentHash = shippedHash;
        ctx.SaveChanges();
    }

}
