using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionScreens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_screens",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DirectorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RowsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CursorRow = table.Column<int>(type: "INTEGER", nullable: false),
                    CursorCol = table.Column<int>(type: "INTEGER", nullable: false),
                    CursorVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAlternateScreen = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasGrid = table.Column<bool>(type: "INTEGER", nullable: false),
                    BufferBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ActivityState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Agent = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_screens", x => new { x.tenant_id, x.SessionId, x.CapturedAtUtc });
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_screens_tenant_id",
                table: "session_screens",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_screens_tenant_id_ReceivedAtUtc",
                table: "session_screens",
                columns: new[] { "tenant_id", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_screens");
        }
    }
}
