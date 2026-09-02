using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_rule_firings",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScreenText = table.Column<string>(type: "text", nullable: false),
                    Understanding = table.Column<string>(type: "text", nullable: false),
                    Decision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    TypedText = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    Grounding = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    PrimitiveRuns = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_rule_firings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "session_rules",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Instruction = table.Column<string>(type: "text", nullable: false),
                    ScreenDescription = table.Column<string>(type: "text", nullable: false),
                    TriggerWords = table.Column<List<string>>(type: "text[]", nullable: false),
                    ScopeAgent = table.Column<string>(type: "text", nullable: true),
                    ScopeRepository = table.Column<string>(type: "text", nullable: true),
                    ScopeMachine = table.Column<string>(type: "text", nullable: true),
                    ScopeMission = table.Column<string>(type: "text", nullable: true),
                    CooldownSeconds = table.Column<int>(type: "integer", nullable: false),
                    DailyCap = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PromotedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Calls = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_firings_tenant_id",
                schema: "gateway",
                table: "session_rule_firings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_firings_tenant_id_RuleId_OccurredUtc",
                schema: "gateway",
                table: "session_rule_firings",
                columns: new[] { "tenant_id", "RuleId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_firings_tenant_id_SessionId_OccurredUtc",
                schema: "gateway",
                table: "session_rule_firings",
                columns: new[] { "tenant_id", "SessionId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_rules_tenant_id",
                schema: "gateway",
                table: "session_rules",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_rules_tenant_id_CreatedUtc",
                schema: "gateway",
                table: "session_rules",
                columns: new[] { "tenant_id", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_rule_firings",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "session_rules",
                schema: "gateway");
        }
    }
}
