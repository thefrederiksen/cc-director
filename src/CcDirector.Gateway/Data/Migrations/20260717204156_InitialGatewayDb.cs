using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialGatewayDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cron_jobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduleKind = table.Column<string>(type: "TEXT", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: true),
                    RunAt = table.Column<string>(type: "TEXT", nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    PreventOverlap = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyOn = table.Column<string>(type: "TEXT", nullable: false),
                    NotifyWebhookUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastFiredUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cron_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cron_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduledUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FiredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Machine = table.Column<string>(type: "TEXT", nullable: false),
                    TargetDirectorId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    InfraStatus = table.Column<string>(type: "TEXT", nullable: false),
                    TaskStatus = table.Column<string>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cron_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cron_jobs_tenant_id",
                table: "cron_jobs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_JobId_Sequence",
                table: "cron_runs",
                columns: new[] { "JobId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_tenant_id",
                table: "cron_runs",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cron_jobs");

            migrationBuilder.DropTable(
                name: "cron_runs");
        }
    }
}
