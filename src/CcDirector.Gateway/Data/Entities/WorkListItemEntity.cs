namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One ordered item reference (<see cref="Contracts.WorkListItemRef"/>) inside a named work list: a row in
/// the <c>worklist_items</c> child table. Grouped by <see cref="WorkListId"/> (a foreign key to the owning
/// <see cref="WorkListEntity"/>) and returned in ascending <see cref="Position"/> order, so reorder and
/// targeted remove-by-source-and-id stay exact and queryable - the child-table pattern the cron run history
/// established.
/// </summary>
public sealed class WorkListItemEntity : GatewayMintedKeyEntity
{
    /// <summary>The owning work list's id (foreign key).</summary>
    public Guid WorkListId { get; set; }

    /// <summary>Order within the list, assigned in code. Items are read in ascending Position.</summary>
    public int Position { get; set; }

    /// <summary>The source system the item lives in (github / devops / jira / ...).</summary>
    public string Source { get; set; } = "";

    /// <summary>The item identifier within its source (a string; maps to <see cref="Contracts.WorkListItemRef.Id"/>).</summary>
    public string ItemId { get; set; } = "";

    /// <summary>Optional free-text grouping label for display.</summary>
    public string? Area { get; set; }
}
