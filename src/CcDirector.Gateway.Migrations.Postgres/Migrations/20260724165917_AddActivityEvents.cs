using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activity_events",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectorSequence = table.Column<long>(type: "bigint", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DirectorId = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    Machine = table.Column<string>(type: "text", nullable: true),
                    AgentKind = table.Column<string>(type: "text", nullable: true),
                    ContextId = table.Column<string>(type: "text", nullable: true),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    PreviousState = table.Column<string>(type: "text", nullable: true),
                    NewState = table.Column<string>(type: "text", nullable: true),
                    Cause = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    InputOrigin = table.Column<string>(type: "text", nullable: true),
                    SendSource = table.Column<string>(type: "text", nullable: true),
                    DetectorMode = table.Column<string>(type: "text", nullable: true),
                    DetectorVersion = table.Column<string>(type: "text", nullable: true),
                    OutputByteCount = table.Column<long>(type: "bigint", nullable: true),
                    BeforeScreenHash = table.Column<string>(type: "text", nullable: true),
                    AfterScreenHash = table.Column<string>(type: "text", nullable: true),
                    BoundedScreenDiff = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_events", x => new { x.tenant_id, x.EventId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_tenant_id",
                schema: "gateway",
                table: "activity_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_tenant_id_DirectorId_DirectorSequence",
                schema: "gateway",
                table: "activity_events",
                columns: new[] { "tenant_id", "DirectorId", "DirectorSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_tenant_id_EventType_OccurredUtc",
                schema: "gateway",
                table: "activity_events",
                columns: new[] { "tenant_id", "EventType", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_tenant_id_SessionId_OccurredUtc",
                schema: "gateway",
                table: "activity_events",
                columns: new[] { "tenant_id", "SessionId", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_events",
                schema: "gateway");
        }
    }
}
