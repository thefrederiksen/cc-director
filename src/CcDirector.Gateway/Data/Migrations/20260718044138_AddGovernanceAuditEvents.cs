using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernanceAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "governance_audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governance_audit_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_governance_audit_events_tenant_id",
                table: "governance_audit_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_governance_audit_events_tenant_id_Category_OccurredUtc",
                table: "governance_audit_events",
                columns: new[] { "tenant_id", "Category", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_governance_audit_events_tenant_id_RunId_OccurredUtc",
                table: "governance_audit_events",
                columns: new[] { "tenant_id", "RunId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_governance_audit_events_tenant_id_SessionId_OccurredUtc",
                table: "governance_audit_events",
                columns: new[] { "tenant_id", "SessionId", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "governance_audit_events");
        }
    }
}
