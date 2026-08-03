using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Tests for the EF data-layer backbone (Hosted Gateway mission, Step 1b): the database opens and migrates
/// on a fresh file, fails loud on a bad file (no JSON fallback), and enforces tenant isolation through the
/// global query filter - one tenant never reads another tenant's rows, exercised end-to-end through
/// <see cref="CronJobStore"/>.
/// </summary>
public sealed class GatewayDatabaseTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static CronJobDto Job(string name) => new()
    {
        Name = name,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "m" },
        Action = new CronJobAction { RepoPath = @"D:\r", Seed = "/x" },
    };

    [Fact]
    public void FreshFile_OpensAndMigrates()
    {
        using var db = _h.Open();
        // The store construction exercises the migrated schema; an empty store on a fresh DB is the proof.
        Assert.Empty(new CronJobStore(db, _h.LegacyPath("none.json")).ListAll());
        Assert.True(File.Exists(_h.DbPath));
    }

    [Fact]
    public void BadDatabaseFile_FailsLoud_NoFallback()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_h.DbPath)!);
        File.WriteAllText(_h.DbPath, "this is not a SQLite database");

        var ex = Assert.Throws<InvalidOperationException>(() => _h.Open());
        Assert.Contains("could not be opened or migrated", ex.Message);
    }

    [Fact]
    public void Tenant_Isolation_OneTenantCannotSeeAnothersRows()
    {
        var legacyLocal = _h.LegacyPath("local.json");
        var legacyOther = _h.LegacyPath("other.json");

        // Tenant "local" writes a job.
        var local = new CronJobStore(_h.Open(new SingleTenantContext()), legacyLocal);
        var localJob = local.Create(Job("local-job"));

        // A DIFFERENT tenant over the SAME database file sees none of it (the global query filter).
        var other = new CronJobStore(_h.Open(new FixedTenantContext(new TenantId("other-tenant"))), legacyOther);
        Assert.Empty(other.ListAll());
        Assert.Null(other.Get(localJob.Id));

        // And a job the other tenant writes is invisible to "local".
        var otherJob = other.Create(Job("other-job"));
        Assert.Null(local.Get(otherJob.Id));
        Assert.Single(local.ListAll());
        Assert.Equal("local-job", local.ListAll()[0].Name);

        // Each tenant sees exactly its own one row.
        Assert.Single(other.ListAll());
        Assert.Equal("other-job", other.ListAll()[0].Name);
    }
}
