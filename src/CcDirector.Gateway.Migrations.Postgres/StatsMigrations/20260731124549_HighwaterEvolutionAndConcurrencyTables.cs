using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.StatsMigrations
{
    /// <summary>
    /// The Postgres statistics chain catches up with the model in one additive step: the previous-value and
    /// generation columns on the three high-water tables (schema versions 6 and 7 of the SQLite store, in
    /// its own chain split per-version for the PRAGMA user_version stamp rule - Postgres has no such stamp,
    /// so one migration is honest here) plus the three concurrency tables that replace
    /// gateway-concurrency-stats.json. Everything is ADD: new columns with defaults and new tables, no
    /// rewrite of existing rows, all inside the gateway_stats schema with its own history table.
    /// </summary>
    public partial class HighwaterEvolutionAndConcurrencyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "generation",
                schema: "gateway_stats",
                table: "token_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_cache_creation_tokens",
                schema: "gateway_stats",
                table: "token_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_cache_read_tokens",
                schema: "gateway_stats",
                table: "token_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_input_tokens",
                schema: "gateway_stats",
                table: "token_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_output_tokens",
                schema: "gateway_stats",
                table: "token_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "generation",
                schema: "gateway_stats",
                table: "session_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_chars",
                schema: "gateway_stats",
                table: "session_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_turns",
                schema: "gateway_stats",
                table: "session_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "generation",
                schema: "gateway_stats",
                table: "agent_driven_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_chars",
                schema: "gateway_stats",
                table: "agent_driven_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_turns",
                schema: "gateway_stats",
                table: "agent_driven_highwater",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "concurrency_hour",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    hour_utc = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    max_live = table.Column<int>(type: "integer", nullable: false),
                    max_working = table.Column<int>(type: "integer", nullable: false),
                    distinct_sessions = table.Column<int>(type: "integer", nullable: false),
                    distinct_machines = table.Column<int>(type: "integer", nullable: false),
                    distinct_repos = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_concurrency_hour", x => new { x.tenant, x.hour_utc });
                });

            migrationBuilder.CreateTable(
                name: "concurrency_hour_member",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    hour_utc = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    kind = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    member_id = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_concurrency_hour_member", x => new { x.tenant, x.hour_utc, x.kind, x.member_id });
                });

            migrationBuilder.CreateTable(
                name: "concurrency_peak",
                schema: "gateway_stats",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    live_max = table.Column<int>(type: "integer", nullable: false),
                    live_max_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    working_max = table.Column<int>(type: "integer", nullable: false),
                    working_max_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_concurrency_peak", x => x.tenant);
                });

            migrationBuilder.CreateIndex(
                name: "ux_repo_identity_tenant_display",
                schema: "gateway_stats",
                table: "repo_identity",
                columns: new[] { "tenant", "repo_display" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_model_identity_tenant_display",
                schema: "gateway_stats",
                table: "model_identity",
                columns: new[] { "tenant", "model_display" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_checkout_identity_tenant_display",
                schema: "gateway_stats",
                table: "checkout_identity",
                columns: new[] { "tenant", "checkout_display" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_agent_identity_tenant_display",
                schema: "gateway_stats",
                table: "agent_identity",
                columns: new[] { "tenant", "agent_display" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "concurrency_hour",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "concurrency_hour_member",
                schema: "gateway_stats");

            migrationBuilder.DropTable(
                name: "concurrency_peak",
                schema: "gateway_stats");

            migrationBuilder.DropIndex(
                name: "ux_repo_identity_tenant_display",
                schema: "gateway_stats",
                table: "repo_identity");

            migrationBuilder.DropIndex(
                name: "ux_model_identity_tenant_display",
                schema: "gateway_stats",
                table: "model_identity");

            migrationBuilder.DropIndex(
                name: "ux_checkout_identity_tenant_display",
                schema: "gateway_stats",
                table: "checkout_identity");

            migrationBuilder.DropIndex(
                name: "ux_agent_identity_tenant_display",
                schema: "gateway_stats",
                table: "agent_identity");

            migrationBuilder.DropColumn(
                name: "generation",
                schema: "gateway_stats",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_cache_creation_tokens",
                schema: "gateway_stats",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_cache_read_tokens",
                schema: "gateway_stats",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_input_tokens",
                schema: "gateway_stats",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_output_tokens",
                schema: "gateway_stats",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "generation",
                schema: "gateway_stats",
                table: "session_highwater");

            migrationBuilder.DropColumn(
                name: "previous_chars",
                schema: "gateway_stats",
                table: "session_highwater");

            migrationBuilder.DropColumn(
                name: "previous_turns",
                schema: "gateway_stats",
                table: "session_highwater");

            migrationBuilder.DropColumn(
                name: "generation",
                schema: "gateway_stats",
                table: "agent_driven_highwater");

            migrationBuilder.DropColumn(
                name: "previous_chars",
                schema: "gateway_stats",
                table: "agent_driven_highwater");

            migrationBuilder.DropColumn(
                name: "previous_turns",
                schema: "gateway_stats",
                table: "agent_driven_highwater");
        }
    }
}
