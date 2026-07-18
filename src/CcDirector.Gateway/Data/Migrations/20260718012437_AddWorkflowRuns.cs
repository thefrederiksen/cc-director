using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AcceptanceStatus = table.Column<string>(type: "TEXT", nullable: false),
                    AcceptedBy = table.Column<string>(type: "TEXT", nullable: true),
                    AcceptedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: true),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ParentRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RepoPath = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    CriteriaResults = table.Column<string>(type: "TEXT", nullable: true),
                    Participants = table.Column<string>(type: "TEXT", nullable: true),
                    ProofLinks = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_MissionId",
                table: "workflow_runs",
                column: "MissionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_tenant_id",
                table: "workflow_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkflowId",
                table: "workflow_runs",
                column: "WorkflowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_runs");
        }
    }
}
