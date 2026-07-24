using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRepoState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repo_state",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    DirectorId = table.Column<string>(type: "text", nullable: false),
                    RepoPath = table.Column<string>(type: "text", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DefaultBranch = table.Column<string>(type: "text", nullable: true),
                    CurrentBranch = table.Column<string>(type: "text", nullable: true),
                    IsDirty = table.Column<bool>(type: "boolean", nullable: false),
                    CollectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BranchesJson = table.Column<string>(type: "text", nullable: false),
                    WorktreesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_state", x => new { x.tenant_id, x.DirectorId, x.RepoPath });
                });

            migrationBuilder.CreateIndex(
                name: "IX_repo_state_tenant_id",
                schema: "gateway",
                table: "repo_state",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_repo_state_tenant_id_ReceivedAtUtc",
                schema: "gateway",
                table: "repo_state",
                columns: new[] { "tenant_id", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repo_state",
                schema: "gateway");
        }
    }
}
