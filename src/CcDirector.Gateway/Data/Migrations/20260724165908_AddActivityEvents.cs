using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activity_events",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DirectorSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Machine = table.Column<string>(type: "TEXT", nullable: true),
                    AgentKind = table.Column<string>(type: "TEXT", nullable: true),
                    ContextId = table.Column<string>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousState = table.Column<string>(type: "TEXT", nullable: true),
                    NewState = table.Column<string>(type: "TEXT", nullable: true),
                    Cause = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    InputOrigin = table.Column<string>(type: "TEXT", nullable: true),
                    SendSource = table.Column<string>(type: "TEXT", nullable: true),
                    DetectorMode = table.Column<string>(type: "TEXT", nullable: true),
                    DetectorVersion = table.Column<string>(type: "TEXT", nullable: true),
                    OutputByteCount = table.Column<long>(type: "INTEGER", nullable: true),
                    BeforeScreenHash = table.Column<string>(type: "TEXT", nullable: true),
                    AfterScreenHash = table.Column<string>(type: "TEXT", nullable: true),
                    BoundedScreenDiff = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_events", x => new { x.tenant_id, x.EventId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_tenant_id",
                table: "activity_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_tenant_id_DirectorId_DirectorSequence",
                table: "activity_events",
                columns: new[] { "tenant_id", "DirectorId", "DirectorSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_tenant_id_EventType_OccurredUtc",
                table: "activity_events",
                columns: new[] { "tenant_id", "EventType", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_tenant_id_SessionId_OccurredUtc",
                table: "activity_events",
                columns: new[] { "tenant_id", "SessionId", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_events");
        }
    }
}
