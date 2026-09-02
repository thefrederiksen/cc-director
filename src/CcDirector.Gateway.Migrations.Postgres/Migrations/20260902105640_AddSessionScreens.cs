using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionScreens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_screens",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DirectorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RowsJson = table.Column<string>(type: "text", nullable: false),
                    CursorRow = table.Column<int>(type: "integer", nullable: false),
                    CursorCol = table.Column<int>(type: "integer", nullable: false),
                    CursorVisible = table.Column<bool>(type: "boolean", nullable: false),
                    IsAlternateScreen = table.Column<bool>(type: "boolean", nullable: false),
                    HasGrid = table.Column<bool>(type: "boolean", nullable: false),
                    BufferBytes = table.Column<long>(type: "bigint", nullable: false),
                    ActivityState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Agent = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_screens", x => new { x.tenant_id, x.SessionId, x.CapturedAtUtc });
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_screens_tenant_id",
                schema: "gateway",
                table: "session_screens",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_screens_tenant_id_ReceivedAtUtc",
                schema: "gateway",
                table: "session_screens",
                columns: new[] { "tenant_id", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_screens",
                schema: "gateway");
        }
    }
}
