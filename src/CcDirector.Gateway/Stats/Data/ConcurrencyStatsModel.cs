using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// The model configuration for the concurrency store's three tables (<c>concurrency_peak</c>,
/// <c>concurrency_hour</c>, <c>concurrency_hour_member</c>).
///
/// It lives in its own file, as one call, so that merging it into the full sixteen-table
/// <see cref="GatewayStatsDbContext"/> is a single line rather than a hand-reconciled block: the context
/// declares the three <c>DbSet</c>s and calls <see cref="Configure"/>.
///
/// Table and column names are snake_case and are configured EXPLICITLY, never by a naming convention, in
/// line with the Step 2 entity contract: the self-host statistics store already exists on disk with these
/// names and a convention change must not be able to rename a column out from under it.
/// </summary>
public static class ConcurrencyStatsModel
{
    /// <summary>
    /// Store UTC and read back UTC. Without this the Kind is lost on the round trip and a timestamp read
    /// from the store would render as a local time on one machine and a UTC time on another. Same converter
    /// pair the Gateway's main context applies model-wide; declared here so these three tables carry it
    /// whether or not the context they are merged into has its own convention.
    /// </summary>
    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter = new(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    /// <summary>Configure the three concurrency tables on <paramref name="modelBuilder"/>.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ConcurrencyPeakEntity>(b =>
        {
            b.ToTable("concurrency_peak");
            // One row per tenant: the tenant IS the key. One account's "how many at once" can never mix with
            // another's because there is nowhere for it to mix.
            b.HasKey(e => e.Tenant);
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.LiveMax).HasColumnName("live_max");
            b.Property(e => e.LiveMaxAtUtc).HasColumnName("live_max_at_utc").HasConversion(NullableUtcConverter);
            b.Property(e => e.WorkingMax).HasColumnName("working_max");
            b.Property(e => e.WorkingMaxAtUtc).HasColumnName("working_max_at_utc").HasConversion(NullableUtcConverter);
        });

        modelBuilder.Entity<ConcurrencyHourEntity>(b =>
        {
            b.ToTable("concurrency_hour");
            b.HasKey(e => new { e.Tenant, e.HourUtc });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            // A string hour key, not a timestamp - the contract is explicit that this does not become a
            // timestamp. Fixed-width and zero-padded, so text order is time order.
            b.Property(e => e.HourUtc).HasColumnName("hour_utc").IsRequired();
            b.Property(e => e.MaxLive).HasColumnName("max_live");
            b.Property(e => e.MaxWorking).HasColumnName("max_working");
            b.Property(e => e.DistinctSessions).HasColumnName("distinct_sessions");
            b.Property(e => e.DistinctMachines).HasColumnName("distinct_machines");
            b.Property(e => e.DistinctRepos).HasColumnName("distinct_repos");
        });

        modelBuilder.Entity<ConcurrencyHourMemberEntity>(b =>
        {
            b.ToTable("concurrency_hour_member");
            // The whole row is the key - there is no payload. Deliberately ORDINAL, on both providers: this
            // table records raw spellings for restart durability and is never asked whether two of them mean
            // the same machine. The in-memory comparers answer that. See ConcurrencyHourMemberEntity.
            b.HasKey(e => new { e.Tenant, e.HourUtc, e.Kind, e.MemberId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.HourUtc).HasColumnName("hour_utc").IsRequired();
            b.Property(e => e.Kind).HasColumnName("kind").IsRequired();
            b.Property(e => e.MemberId).HasColumnName("member_id").IsRequired();
        });
    }
}
