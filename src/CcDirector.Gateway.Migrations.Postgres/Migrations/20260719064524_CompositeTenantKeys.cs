using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CompositeTenantKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_workflows",
                schema: "gateway",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_workflow_versions_WorkflowId_Version",
                schema: "gateway",
                table: "workflow_versions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_mission_notes",
                schema: "gateway",
                table: "mission_notes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_cron_jobs",
                schema: "gateway",
                table: "cron_jobs");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workflows",
                schema: "gateway",
                table: "workflows",
                columns: new[] { "tenant_id", "Id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_mission_notes",
                schema: "gateway",
                table: "mission_notes",
                columns: new[] { "tenant_id", "Key" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_cron_jobs",
                schema: "gateway",
                table: "cron_jobs",
                columns: new[] { "tenant_id", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_tenant_id_WorkflowId_Version",
                schema: "gateway",
                table: "workflow_versions",
                columns: new[] { "tenant_id", "WorkflowId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_workflows",
                schema: "gateway",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_workflow_versions_tenant_id_WorkflowId_Version",
                schema: "gateway",
                table: "workflow_versions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_mission_notes",
                schema: "gateway",
                table: "mission_notes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_cron_jobs",
                schema: "gateway",
                table: "cron_jobs");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workflows",
                schema: "gateway",
                table: "workflows",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_mission_notes",
                schema: "gateway",
                table: "mission_notes",
                column: "Key");

            migrationBuilder.AddPrimaryKey(
                name: "PK_cron_jobs",
                schema: "gateway",
                table: "cron_jobs",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_WorkflowId_Version",
                schema: "gateway",
                table: "workflow_versions",
                columns: new[] { "WorkflowId", "Version" },
                unique: true);
        }
    }
}
