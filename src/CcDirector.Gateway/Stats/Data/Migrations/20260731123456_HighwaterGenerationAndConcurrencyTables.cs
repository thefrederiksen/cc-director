using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Stats.Data.Migrations
{
    /// <summary>
    /// Schema version 7 of the SQLite statistics store, as an Entity Framework migration: the generation
    /// (incarnation) column on the three high-water tables, matching the shipped hand-rolled
    /// <c>GatewayStatsDatabase.MigrateToVersion7</c> - plus the three concurrency tables, which have no
    /// hand-rolled counterpart because the concurrency record previously lived in
    /// <c>gateway-concurrency-stats.json</c> and moves into the statistics database with this chain. Stamps
    /// <c>PRAGMA user_version = 7</c> in its Up() and resets it in its Down(), per the rule
    /// <c>GatewayStatsSqliteVersionStampTests</c> enforces.
    /// </summary>
    public partial class HighwaterGenerationAndConcurrencyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "generation",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "generation",
                table: "session_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "generation",
                table: "agent_driven_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "concurrency_hour",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    hour_utc = table.Column<string>(type: "TEXT", nullable: false),
                    max_live = table.Column<int>(type: "INTEGER", nullable: false),
                    max_working = table.Column<int>(type: "INTEGER", nullable: false),
                    distinct_sessions = table.Column<int>(type: "INTEGER", nullable: false),
                    distinct_machines = table.Column<int>(type: "INTEGER", nullable: false),
                    distinct_repos = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_concurrency_hour", x => new { x.tenant, x.hour_utc });
                });

            migrationBuilder.CreateTable(
                name: "concurrency_hour_member",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    hour_utc = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    member_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_concurrency_hour_member", x => new { x.tenant, x.hour_utc, x.kind, x.member_id });
                });

            migrationBuilder.CreateTable(
                name: "concurrency_peak",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    live_max = table.Column<int>(type: "INTEGER", nullable: false),
                    live_max_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    working_max = table.Column<int>(type: "INTEGER", nullable: false),
                    working_max_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_concurrency_peak", x => x.tenant);
                });

            migrationBuilder.Sql("PRAGMA user_version = 7");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "concurrency_hour");

            migrationBuilder.DropTable(
                name: "concurrency_hour_member");

            migrationBuilder.DropTable(
                name: "concurrency_peak");

            migrationBuilder.DropColumn(
                name: "generation",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "generation",
                table: "session_highwater");

            migrationBuilder.DropColumn(
                name: "generation",
                table: "agent_driven_highwater");

            migrationBuilder.Sql("PRAGMA user_version = 6");
        }
    }
}
