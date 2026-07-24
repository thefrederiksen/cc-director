using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepoState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repo_state",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", nullable: false),
                    RepoPath = table.Column<string>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentBranch = table.Column<string>(type: "TEXT", nullable: true),
                    IsDirty = table.Column<bool>(type: "INTEGER", nullable: false),
                    CollectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BranchesJson = table.Column<string>(type: "TEXT", nullable: false),
                    WorktreesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_state", x => new { x.tenant_id, x.DirectorId, x.RepoPath });
                });

            migrationBuilder.CreateIndex(
                name: "IX_repo_state_tenant_id",
                table: "repo_state",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_repo_state_tenant_id_ReceivedAtUtc",
                table: "repo_state",
                columns: new[] { "tenant_id", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repo_state");
        }
    }
}
