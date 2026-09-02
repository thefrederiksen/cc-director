using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddKnownRepositories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "known_repositories",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, collation: "C"),
                    PathKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, collation: "C"),
                    MachineName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_known_repositories", x => x.Id);
                });

            // Recover every machine-associated repository still present in retained session history.
            // The durable catalog is not part of the history retention sweep, so these facts survive the
            // later deletion of their source rows. gen_random_uuid is built into supported PostgreSQL
            // versions and mints an identity independent of every tenant-supplied fact.
            migrationBuilder.Sql(
                """
                INSERT INTO "gateway"."known_repositories"
                    ("Id", "MachineKey", "PathKey", "MachineName", "Path", "Name", "LastUsedUtc", "tenant_id")
                SELECT
                    gen_random_uuid(),
                    translate(TRIM("MachineName"),
                        'abcdefghijklmnopqrstuvwxyz',
                        'ABCDEFGHIJKLMNOPQRSTUVWXYZ'),
                    TRIM("RepoPath"),
                    TRIM("MachineName"),
                    TRIM("RepoPath"),
                    COALESCE(MAX(NULLIF(TRIM(COALESCE("RepoName", '')), '')), ''),
                    MAX("LastSeenUtc"),
                    "tenant_id"
                FROM "gateway"."session_history"
                WHERE "MachineName" IS NOT NULL
                  AND TRIM("MachineName") <> ''
                  AND "RepoPath" IS NOT NULL
                  AND TRIM("RepoPath") <> ''
                GROUP BY "tenant_id", TRIM("MachineName"), TRIM("RepoPath");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_known_repositories_tenant_id",
                schema: "gateway",
                table: "known_repositories",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_known_repositories_tenant_id_MachineKey_LastUsedUtc",
                schema: "gateway",
                table: "known_repositories",
                columns: new[] { "tenant_id", "MachineKey", "LastUsedUtc" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "known_repositories",
                schema: "gateway");
        }
    }
}
