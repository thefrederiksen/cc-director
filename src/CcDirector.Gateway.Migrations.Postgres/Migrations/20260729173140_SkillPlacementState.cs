using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SkillPlacementState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill_placement_state",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    DirectorId = table.Column<string>(type: "text", nullable: false),
                    AgentKind = table.Column<string>(type: "text", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    Held = table.Column<int>(type: "integer", nullable: false),
                    Reachable = table.Column<int>(type: "integer", nullable: false),
                    StoreMissing = table.Column<bool>(type: "boolean", nullable: false),
                    ProblemsJson = table.Column<string>(type: "text", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_placement_state", x => new { x.tenant_id, x.DirectorId, x.AgentKind });
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_placement_state_tenant_id",
                schema: "gateway",
                table: "skill_placement_state",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_placement_state_tenant_id_ReceivedAtUtc",
                schema: "gateway",
                table: "skill_placement_state",
                columns: new[] { "tenant_id", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skill_placement_state",
                schema: "gateway");
        }
    }
}
