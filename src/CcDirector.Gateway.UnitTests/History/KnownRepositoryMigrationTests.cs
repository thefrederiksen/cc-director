using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

public sealed class KnownRepositoryMigrationTests
{
    [Fact]
    public void AddKnownRepositories_RetainedHistoryExists_BackfillsAndOutlivesTheSourceRow()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "cc-known-repository-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "gateway.db");

        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>()
                .UseSqlite("Data Source=" + path + ";Pooling=False")
                .Options;
            using var context = new GatewayDbContext(options)
            {
                ActiveTenant = TenantId.Local.Value,
            };
            var migrator = context.Database.GetService<IMigrator>();

            // Build the real schema immediately before this feature's migration, then place a retained
            // history row into it. The final migrate call must create and backfill the catalog.
            migrator.Migrate("20260902003414_AddSessionTurns");
            var lastSeen = new DateTime(2026, 8, 31, 14, 30, 0, DateTimeKind.Utc);
            const string machineName = "Søren_North";
            context.SessionHistory.Add(new SessionHistoryEntity
            {
                TenantId = TenantId.Local.Value,
                SessionId = "backfill-session",
                DirectorId = "backfill-director",
                MachineName = machineName,
                RepoPath = @"D:\Repositories\historical",
                RepoName = "Historical repository",
                StartedAtUtc = lastSeen.AddHours(-1),
                LastSeenUtc = lastSeen,
            });
            context.SaveChanges();

            migrator.Migrate();
            context.ChangeTracker.Clear();

            var catalogRow = Assert.Single(context.KnownRepositories.AsNoTracking());
            Assert.Equal(machineName, catalogRow.MachineName);
            Assert.Equal(@"D:\Repositories\historical", catalogRow.Path);
            Assert.Equal("Historical repository", catalogRow.Name);
            Assert.Equal(lastSeen, catalogRow.LastUsedUtc);

            context.SessionHistory.Remove(Assert.Single(context.SessionHistory));
            context.SaveChanges();
            context.ChangeTracker.Clear();

            Assert.Single(context.KnownRepositories.AsNoTracking());

            using var database = new GatewayDatabase(new SingleTenantContext(), path);
            var store = new KnownRepositoryStore(database);
            var retained = Assert.Single(store.ReadForMachine(TenantId.Local, machineName));
            Assert.Equal(@"D:\Repositories\historical", retained.Path);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a throwaway migration database.
            }
        }
    }
}
