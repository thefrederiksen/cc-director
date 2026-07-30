namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// One tenant's ALL-TIME concurrency peaks (<c>concurrency_peak</c>), one row per tenant: the highest
/// number of live (non-exited) sessions ever seen at once, the highest number working at once, and the
/// instant each of those peaks was set.
///
/// Every column here only ever GROWS. Both maxima are written with an explicit
/// <c>ON CONFLICT DO UPDATE ... GREATEST</c>, and each timestamp moves ONLY on the write where its own
/// maximum actually advanced. A change-tracked read-then-save would be a lost-update generator the moment
/// two Gateway containers observe the same roster - which is exactly the state a slot swap puts us in - so
/// nothing writes these columns through the change tracker. See
/// <see cref="Stats.GatewaySessionConcurrencyStore"/> for the statements.
///
/// The two CURRENT values (live now, working now) are deliberately NOT here. They are runtime-only, they
/// reset to zero when the process restarts, and the JSON store this table replaces had no field for them
/// either. Persisting them would make a restarted container report a stale "right now" number.
/// </summary>
public sealed class ConcurrencyPeakEntity
{
    /// <summary>The owning tenant (the raw <see cref="Core.Tenancy.TenantId.Value"/>). The whole primary key:
    /// one peak row per tenant.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>The highest number of live (non-exited) sessions seen at once, all time.</summary>
    public int LiveMax { get; set; }

    /// <summary>When <see cref="LiveMax"/> was set. Null until a live peak above zero has been observed -
    /// a tenant whose roster has only ever been empty has no instant to name.</summary>
    public DateTime? LiveMaxAtUtc { get; set; }

    /// <summary>The highest number of sessions seen working (agent processing a turn) at once, all time.</summary>
    public int WorkingMax { get; set; }

    /// <summary>When <see cref="WorkingMax"/> was set. Null until a working peak above zero has been
    /// observed.</summary>
    public DateTime? WorkingMaxAtUtc { get; set; }
}
