using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernanceEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "governance_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectKind = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governance_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_governance_events_tenant_id",
                table: "governance_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_governance_events_tenant_id_OccurredUtc",
                table: "governance_events",
                columns: new[] { "tenant_id", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_governance_events_tenant_id_RunId_OccurredUtc",
                table: "governance_events",
                columns: new[] { "tenant_id", "RunId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_governance_events_tenant_id_SessionId_OccurredUtc",
                table: "governance_events",
                columns: new[] { "tenant_id", "SessionId", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "governance_events");
        }
    }
}
