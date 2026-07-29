using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class SkillPlacementState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill_placement_state",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentKind = table.Column<string>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: false),
                    Held = table.Column<int>(type: "INTEGER", nullable: false),
                    Reachable = table.Column<int>(type: "INTEGER", nullable: false),
                    StoreMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProblemsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_placement_state", x => new { x.tenant_id, x.DirectorId, x.AgentKind });
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_placement_state_tenant_id",
                table: "skill_placement_state",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_placement_state_tenant_id_ReceivedAtUtc",
                table: "skill_placement_state",
                columns: new[] { "tenant_id", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skill_placement_state");
        }
    }
}
