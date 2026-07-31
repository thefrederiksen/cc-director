using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Stats.Data.Migrations
{
    /// <inheritdoc />
    public partial class StatsV6PreviousColumnsIdentityIndexesAndConcurrencyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "previous_cache_creation_tokens",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_cache_read_tokens",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_input_tokens",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_output_tokens",
                table: "token_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_chars",
                table: "session_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_turns",
                table: "session_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_chars",
                table: "agent_driven_highwater",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "previous_turns",
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

            migrationBuilder.CreateIndex(
                name: "ux_repo_identity_tenant_display",
                table: "repo_identity",
                columns: new[] { "tenant", "repo_display" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_model_identity_tenant_display",
                table: "model_identity",
                columns: new[] { "tenant", "model_display" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_checkout_identity_tenant_display",
                table: "checkout_identity",
                columns: new[] { "tenant", "checkout_display" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_agent_identity_tenant_display",
                table: "agent_identity",
                columns: new[] { "tenant", "agent_display" },
                unique: true);
        
            // THE VERSION STAMP IS PART OF THE SCHEMA. This migration is schema version 6, so it moves the stamp the hand-rolled path moves at MigrateToVersion6. An older build reads this to decide whether it understands the file at all; leaving it behind lets that build run its own steps against tables that already exist.
            migrationBuilder.Sql("PRAGMA user_version = 6");
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

            migrationBuilder.DropIndex(
                name: "ux_repo_identity_tenant_display",
                table: "repo_identity");

            migrationBuilder.DropIndex(
                name: "ux_model_identity_tenant_display",
                table: "model_identity");

            migrationBuilder.DropIndex(
                name: "ux_checkout_identity_tenant_display",
                table: "checkout_identity");

            migrationBuilder.DropIndex(
                name: "ux_agent_identity_tenant_display",
                table: "agent_identity");

            migrationBuilder.DropColumn(
                name: "previous_cache_creation_tokens",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_cache_read_tokens",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_input_tokens",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_output_tokens",
                table: "token_highwater");

            migrationBuilder.DropColumn(
                name: "previous_chars",
                table: "session_highwater");

            migrationBuilder.DropColumn(
                name: "previous_turns",
                table: "session_highwater");

            migrationBuilder.DropColumn(
                name: "previous_chars",
                table: "agent_driven_highwater");

            migrationBuilder.DropColumn(
                name: "previous_turns",
                table: "agent_driven_highwater");
        
            // Down puts the stamp back to 5, so reverting this migration leaves a file an older build still recognises.
            migrationBuilder.Sql("PRAGMA user_version = 5");
        }
    }
}
