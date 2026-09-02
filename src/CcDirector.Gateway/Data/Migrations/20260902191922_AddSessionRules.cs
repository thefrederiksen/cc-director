using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_rule_firings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScreenText = table.Column<string>(type: "TEXT", nullable: false),
                    Understanding = table.Column<string>(type: "TEXT", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    TypedText = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    PrimitiveRuns = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_rule_firings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "session_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", nullable: false),
                    ScreenDescription = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerWords = table.Column<string>(type: "TEXT", nullable: false),
                    ScopeAgent = table.Column<string>(type: "TEXT", nullable: true),
                    ScopeRepository = table.Column<string>(type: "TEXT", nullable: true),
                    ScopeMachine = table.Column<string>(type: "TEXT", nullable: true),
                    ScopeMission = table.Column<string>(type: "TEXT", nullable: true),
                    CooldownSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    DailyCap = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Calls = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_firings_tenant_id",
                table: "session_rule_firings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_firings_tenant_id_RuleId_OccurredUtc",
                table: "session_rule_firings",
                columns: new[] { "tenant_id", "RuleId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_firings_tenant_id_SessionId_OccurredUtc",
                table: "session_rule_firings",
                columns: new[] { "tenant_id", "SessionId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_rules_tenant_id",
                table: "session_rules",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_rules_tenant_id_CreatedUtc",
                table: "session_rules",
                columns: new[] { "tenant_id", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_rule_firings");

            migrationBuilder.DropTable(
                name: "session_rules");
        }
    }
}
