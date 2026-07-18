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
///    a binary that ships newer content auto-publishes it as the next version - the user follows
///    "ours" and gets updates.
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
            var instructions = BuiltInWorkflows.InstructionsFor(definition.Id);
            var steps = definition.Steps.Select(s => new WorkflowStepDto
            {
                Name = s.Name,
                Description = s.Description,
                Doer = s.Doer,
                Reviewer = s.Reviewer,
                Done = s.Done,
            }).ToList();
            var shippedHash = WorkflowContentHash.ForBundle(
                definition.Name, definition.Summary, definition.WhenToUse, definition.HumanCheckpoint,
                steps, Array.Empty<WorkflowOutcomeCriterionDto>(), instructions,
                Array.Empty<(string, string)>());

            var head = ctx.Workflows.FirstOrDefault(h => h.Id == definition.Id);
            if (head is null)
            {
                SeedFresh(ctx, definition, steps, instructions, shippedHash, baseUtc.AddMilliseconds(i));
                continue;
            }

            if (string.Equals(head.ShippedContentHash, shippedHash, StringComparison.Ordinal))
                continue; // this binary ships exactly what was last recorded - nothing to do.

            UpgradeShippedContent(ctx, head, definition, steps, instructions, shippedHash);
        }
    }

    private static void SeedFresh(
        GatewayDbContext ctx,
        WorkflowDefinition definition,
        List<WorkflowStepDto> steps,
        string instructions,
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
        ctx.WorkflowVersions.Add(NewVersionRow(ctx, definition, steps, instructions, shippedHash,
            version: 1, createdUtc));
        ctx.SaveChanges();
        FileLog.Write($"[BuiltInWorkflowSeeder] Seeded built-in workflow: id={definition.Id}, v1, hash={shippedHash[..12]}");
    }

    private static void UpgradeShippedContent(
        GatewayDbContext ctx,
        WorkflowEntity head,
        WorkflowDefinition definition,
        List<WorkflowStepDto> steps,
        string instructions,
        string shippedHash)
    {
        var published = ctx.WorkflowVersions.FirstOrDefault(
            v => v.WorkflowId == head.Id && v.Status == WorkflowVersionStatus.Published);
        var uncustomized = published is not null &&
            string.Equals(published.ContentHash, head.ShippedContentHash, StringComparison.Ordinal);

        if (uncustomized)
        {
            // The user follows the shipped content - publish the newer shipped bundle as the next
            // version so every Director picks it up, exactly like the injected-text "ours" channel.
            var now = DateTime.UtcNow;
            published!.Status = WorkflowVersionStatus.Superseded;
            var nextVersion = head.LatestVersion + 1;
            ctx.WorkflowVersions.Add(NewVersionRow(ctx, definition, steps, instructions, shippedHash,
                nextVersion, now));
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

    private static WorkflowVersionEntity NewVersionRow(
        GatewayDbContext ctx,
        WorkflowDefinition definition,
        List<WorkflowStepDto> steps,
        string instructions,
        string contentHash,
        int version,
        DateTime createdUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = ctx.ActiveTenant!,
        WorkflowId = definition.Id,
        Version = version,
        Status = WorkflowVersionStatus.Published,
        Name = definition.Name,
        Summary = definition.Summary,
        WhenToUse = definition.WhenToUse,
        HumanCheckpoint = definition.HumanCheckpoint,
        Steps = steps.Select(s => new WorkflowStepDto
        {
            Name = s.Name,
            Description = s.Description,
            Doer = s.Doer,
            Reviewer = s.Reviewer,
            Done = s.Done,
        }).ToList(),
        InstructionsMarkdown = instructions,
        OutcomeCriteria = new List<WorkflowOutcomeCriterionDto>(),
        ContentHash = contentHash,
        AuthoredBy = "gateway:shipped",
        ChangeNote = "Shipped built-in content.",
        CreatedUtc = createdUtc,
        PublishedUtc = createdUtc,
    };
}
