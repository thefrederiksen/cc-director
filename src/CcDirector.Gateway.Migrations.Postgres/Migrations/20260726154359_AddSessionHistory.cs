using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_history",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SessionNumber = table.Column<int>(type: "integer", nullable: true),
                    SessionName = table.Column<string>(type: "text", nullable: true),
                    MachineName = table.Column<string>(type: "text", nullable: true),
                    DirectorId = table.Column<string>(type: "text", nullable: false),
                    RepoPath = table.Column<string>(type: "text", nullable: true),
                    RepoName = table.Column<string>(type: "text", nullable: true),
                    AgentKind = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    MissionName = table.Column<string>(type: "text", nullable: true),
                    SessionRole = table.Column<string>(type: "text", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActivityState = table.Column<string>(type: "text", nullable: true),
                    TurnCount = table.Column<long>(type: "bigint", nullable: true),
                    FirstPromptLine = table.Column<string>(type: "text", nullable: true),
                    EndingKind = table.Column<string>(type: "text", nullable: true),
                    EndingLabel = table.Column<string>(type: "text", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SummaryKind = table.Column<string>(type: "text", nullable: true),
                    SummaryIsPartial = table.Column<bool>(type: "boolean", nullable: false),
                    SummaryAttempts = table.Column<int>(type: "integer", nullable: false),
                    SummaryText = table.Column<string>(type: "text", nullable: true),
                    WhatWasBuiltJson = table.Column<string>(type: "text", nullable: true),
                    LeftUnverifiedJson = table.Column<string>(type: "text", nullable: true),
                    BranchesJson = table.Column<string>(type: "text", nullable: true),
                    PullRequestsJson = table.Column<string>(type: "text", nullable: true),
                    CommitsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_history", x => new { x.tenant_id, x.SessionId });
                });

            migrationBuilder.CreateTable(
                name: "session_history_rollups",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    RepoKey = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    DayUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SummaryText = table.Column<string>(type: "text", nullable: true),
                    InputHash = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_history_rollups", x => new { x.tenant_id, x.RepoKey, x.DayUtc });
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_history_tenant_id",
                schema: "gateway",
                table: "session_history",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_history_tenant_id_DirectorId",
                schema: "gateway",
                table: "session_history",
                columns: new[] { "tenant_id", "DirectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_session_history_tenant_id_LastSeenUtc",
                schema: "gateway",
                table: "session_history",
                columns: new[] { "tenant_id", "LastSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_history_rollups_tenant_id",
                schema: "gateway",
                table: "session_history_rollups",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_history_rollups_tenant_id_DayUtc",
                schema: "gateway",
                table: "session_history_rollups",
                columns: new[] { "tenant_id", "DayUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_history",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "session_history_rollups",
                schema: "gateway");
        }
    }
}
