using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_history",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionName = table.Column<string>(type: "TEXT", nullable: true),
                    MachineName = table.Column<string>(type: "TEXT", nullable: true),
                    DirectorId = table.Column<string>(type: "TEXT", nullable: false),
                    RepoPath = table.Column<string>(type: "TEXT", nullable: true),
                    RepoName = table.Column<string>(type: "TEXT", nullable: true),
                    AgentKind = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    MissionName = table.Column<string>(type: "TEXT", nullable: true),
                    SessionRole = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastActivityState = table.Column<string>(type: "TEXT", nullable: true),
                    TurnCount = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstPromptLine = table.Column<string>(type: "TEXT", nullable: true),
                    EndingKind = table.Column<string>(type: "TEXT", nullable: true),
                    EndingLabel = table.Column<string>(type: "TEXT", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SummaryKind = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryIsPartial = table.Column<bool>(type: "INTEGER", nullable: false),
                    SummaryAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    SummaryText = table.Column<string>(type: "TEXT", nullable: true),
                    WhatWasBuiltJson = table.Column<string>(type: "TEXT", nullable: true),
                    LeftUnverifiedJson = table.Column<string>(type: "TEXT", nullable: true),
                    BranchesJson = table.Column<string>(type: "TEXT", nullable: true),
                    PullRequestsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CommitsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_history", x => new { x.tenant_id, x.SessionId });
                });

            migrationBuilder.CreateTable(
                name: "session_history_rollups",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    RepoKey = table.Column<string>(type: "TEXT", nullable: false),
                    DayUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SummaryText = table.Column<string>(type: "TEXT", nullable: true),
                    InputHash = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_history_rollups", x => new { x.tenant_id, x.RepoKey, x.DayUtc });
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_history_tenant_id",
                table: "session_history",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_history_tenant_id_DirectorId",
                table: "session_history",
                columns: new[] { "tenant_id", "DirectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_session_history_tenant_id_LastSeenUtc",
                table: "session_history",
                columns: new[] { "tenant_id", "LastSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_history_rollups_tenant_id",
                table: "session_history_rollups",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_history_rollups_tenant_id_DayUtc",
                table: "session_history_rollups",
                columns: new[] { "tenant_id", "DayUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_history");

            migrationBuilder.DropTable(
                name: "session_history_rollups");
        }
    }
}
