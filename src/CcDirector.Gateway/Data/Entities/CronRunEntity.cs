namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One recorded fire of a cron job (<see cref="Contracts.CronRunRecord"/>) in the EF data layer: a row in
/// the <c>cron_runs</c> child table, one per run. Runs are grouped by <see cref="JobId"/> and returned
/// newest-first, capped at 50 per job (<see cref="CronRunHistoryStore.MaxRecordsPerJob"/>).
///
/// <see cref="JobId"/> is a plain indexed column, NOT a foreign key: run history has an independent
/// lifecycle from the job definition (deleting a job does not delete its run history today), so no cascade
/// relationship is modeled.
///
/// Ordering is by <see cref="Sequence"/>, a code-assigned monotonically increasing value (highest = newest).
/// The JSON store preserved insertion order by prepending; <see cref="Sequence"/> reproduces that exactly
/// and survives the one-time import (records are assigned sequences in their stored newest-first order), so
/// a tie in <see cref="Contracts.CronRunRecord.FiredUtc"/> can never reorder the list.
/// </summary>
public sealed class CronRunEntity : GatewayMintedKeyEntity
{
    /// <summary>The owning cron job's id. Indexed; not a foreign key (independent lifecycle).</summary>
    public string JobId { get; set; } = "";

    /// <summary>Insertion order within a job, assigned in code. Highest is newest; ordering is by this DESC.</summary>
    public long Sequence { get; set; }

    public DateTime ScheduledUtc { get; set; }
    public DateTime FiredUtc { get; set; }
    public string Machine { get; set; } = "";
    public string TargetDirectorId { get; set; } = "";
    public string? SessionId { get; set; }
    public string InfraStatus { get; set; } = "";
    public string TaskStatus { get; set; } = "";
}
