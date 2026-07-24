using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowTenantOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_tenant_overrides",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    WorkflowId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_tenant_overrides", x => new { x.tenant_id, x.WorkflowId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_tenant_overrides_tenant_id",
                schema: "gateway",
                table: "workflow_tenant_overrides",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_tenant_overrides",
                schema: "gateway");
        }
    }
}
