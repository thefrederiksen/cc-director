using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKnownRepositories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "known_repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    PathKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_known_repositories", x => x.Id);
                });

            // Recover every machine-associated repository still present in retained session history.
            // The durable catalog is not part of the history retention sweep, so these facts survive the
            // later deletion of their source rows. The identifier expression mints a fresh canonical Guid
            // inside SQLite for each imported row, matching the Gateway-minted identity used for new rows.
            migrationBuilder.Sql(
                """
                INSERT INTO "known_repositories"
                    ("Id", "MachineKey", "PathKey", "MachineName", "Path", "Name", "LastUsedUtc", "tenant_id")
                SELECT
                    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
                        substr(lower(hex(randomblob(2))), 2) || '-' ||
                        substr('89ab', abs(random()) % 4 + 1, 1) ||
                        substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
                    UPPER(TRIM("MachineName")),
                    TRIM("RepoPath"),
                    TRIM("MachineName"),
                    TRIM("RepoPath"),
                    COALESCE(MAX(NULLIF(TRIM(COALESCE("RepoName", '')), '')), ''),
                    MAX("LastSeenUtc"),
                    "tenant_id"
                FROM "session_history"
                WHERE "MachineName" IS NOT NULL
                  AND TRIM("MachineName") <> ''
                  AND "RepoPath" IS NOT NULL
                  AND TRIM("RepoPath") <> ''
                GROUP BY "tenant_id", TRIM("MachineName"), TRIM("RepoPath");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_known_repositories_tenant_id",
                table: "known_repositories",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_known_repositories_tenant_id_MachineKey_LastUsedUtc",
                table: "known_repositories",
                columns: new[] { "tenant_id", "MachineKey", "LastUsedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "known_repositories");
        }
    }
}
